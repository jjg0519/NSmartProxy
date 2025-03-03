﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using NSmartProxy.Data;
using NSmartProxy.Extension;
using NSmartProxy.Infrastructure;
using NSmartProxy.Authorize;
using NSmartProxy.Data.Config;
using NSmartProxy.Data.Entity;
using NSmartProxy.Interfaces;
using NSmartProxy.Shared;
using static NSmartProxy.Server;
using NSmartProxy.Database;

namespace NSmartProxy
{
    //+------------------------+
    //| NAT                    |
    //|                        |
    //|                        |
    //|    +----------+        |   +-----------+
    //|    |          |        |   |           |
    //|    |  client  |------------>  provider |
    //|    |          |        |   |           |
    //|    +----+-----+        |   +------^----+
    //|         |              |          |
    //|         |              |          |
    //|         |              |          |
    //|    +----V-----+        |          |
    //|    |          |        |          |
    //|    |   IIS    |        |          |
    //|    |          |        |          |
    //|    +----------+        |   +------+-------+
    //|                        |   |              |
    //|                        |   |   consumer   |
    //|                        |   |              |
    //+------------------------+   +--------------+
    public class Server
    {
        public const string USER_DB_PATH = "./nsmart_user";
        public const string SECURE_KEY_FILE_PATH = "./nsmart_sec_key";

        protected ClientConnectionManager ConnectionManager = null;
        protected IDbOperator DbOp;
        protected NSPServerContext ServerContext;

        internal static INSmartLogger Logger; //inject

        public Server(INSmartLogger logger)
        {
            //initialize
            Logger = logger;
            ServerContext = new NSPServerContext();
        }

        /// <summary>
        /// 设置服务端的配置文件，保存服务端配置时可以写入此文件
        /// </summary>
        /// <param name="configPath"></param>
        /// <returns></returns>
        public Server SetServerConfigPath(string configPath) //bad design 
        {
            ServerContext.ServerConfigPath = configPath;
            return this;
        }



        public Server SetAnonymousLogin(bool isSupportAnonymous)
        {
            ServerContext.SupportAnonymousLogin = isSupportAnonymous;
            return this;
        }

        public Server SetConfiguration(NSPServerConfig config)
        {
            ServerContext.ServerConfig = config;
            ServerContext.UpdatePortMap();
            return this;
        }

        //必须设置远程端口才可以通信 //TODO 合并到配置里
        //public Server SetWebPort(int port)
        //{
        //    WebManagementPort = port;
        //    return this;
        //}

        public async Task Start()
        {
            DbOp = new NSmartDbOperator(USER_DB_PATH, USER_DB_PATH + "_index");//加载数据库
            //从配置文件加载服务端配置
            InitSecureKey();
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
            CancellationTokenSource ctsConfig = new CancellationTokenSource();
            CancellationTokenSource ctsHttp = new CancellationTokenSource();
            CancellationTokenSource ctsConsumer = new CancellationTokenSource();

            //1.反向连接池配置
            ConnectionManager = ClientConnectionManager.GetInstance().SetServerContext(ServerContext);
            //注册客户端发生连接时的事件
            ConnectionManager.AppTcpClientMapConfigConnected += ConnectionManager_AppAdded;
            ConnectionManager.ListenServiceClient(DbOp);
            Logger.Debug("NSmart server started");

            //2.开启http服务
            if (ServerContext.ServerConfig.WebAPIPort > 0)
            {
                var httpServer = new HttpServer(Logger, DbOp, ServerContext);
                httpServer.StartHttpService(ctsHttp, ServerContext.ServerConfig.WebAPIPort);
            }

            //3.开启心跳检测线程 
            ProcessHeartbeatsCheck(Global.HeartbeatCheckInterval, ctsConsumer);

            //4.开启配置服务(常开)
            try
            {
                await StartConfigService(ctsConfig);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.Message);
            }
            finally
            {
                Logger.Debug("all closed");
                ctsConfig.Cancel(); ctsHttp.Cancel(); ctsConsumer.Cancel();
                DbOp.Close();
            }
        }

        private void InitSecureKey()
        {
            //生成密钥
            if (File.Exists(SECURE_KEY_FILE_PATH))
            {
                EncryptHelper.AES_Key = File.ReadAllText(SECURE_KEY_FILE_PATH);//prikey
            }
            else
            {
                EncryptHelper.AES_Key = RandomHelper.NextString(8);
                File.WriteAllText(SECURE_KEY_FILE_PATH, EncryptHelper.AES_Key);
            }
        }

        private async Task ProcessHeartbeatsCheck(int interval, CancellationTokenSource cts)
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    //Server.Logger.Debug("开始心跳检测");
                    var outTimeClients = ServerContext.Clients.Where(
                        (cli) => DateTimeHelper.TimeRange(cli.LastUpdateTime, DateTime.Now) > interval).ToList();

                    foreach (var client in outTimeClients)
                    {
                        ServerContext.CloseAllSourceByClient(client.ClientID);
                    }
                    //Server.Logger.Debug("结束心跳检测");
                    await Task.Delay(interval);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message, ex);
            }
            finally
            {
                Logger.Debug("fatal error:心跳检测处理异常终止。");
                //TODO 重新开始
            }
        }


        private void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            Logger.Error(e.Exception.ToString(), e.Exception);
        }


        private async Task StartConfigService(CancellationTokenSource accepting)
        {
            TcpListener listenerConfigService = new TcpListener(IPAddress.Any, ServerContext.ServerConfig.ConfigServicePort);

            Logger.Debug("Listening config request on port " + ServerContext.ServerConfig.ConfigServicePort + "...");
            var taskResultConfig = AcceptConfigRequest(listenerConfigService);

            await taskResultConfig; //block here to hold open the server

        }

        /// <summary>
        /// 有连接连上则开始侦听新的端口
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ConnectionManager_AppAdded(object sender, AppChangedEventArgs e)
        {
            Server.Logger.Debug("AppTcpClientMapReverseConnected事件已触发");
            int port = 0;
            foreach (var kv in ServerContext.PortAppMap)
            {
                if (kv.Value.AppId == e.App.AppId &&
                    kv.Value.ClientId == e.App.ClientId) port = kv.Key;
            }
            if (port == 0) throw new Exception("app未注册");
            var ct = new CancellationToken();

            ListenConsumeAsync(port);
        }

        /// <summary>
        /// 主循环，处理所有来自外部的请求
        /// </summary>
        /// <param name="consumerlistener"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        async Task ListenConsumeAsync(int consumerPort)
        {
            var cts = new CancellationTokenSource();
            var ct = cts.Token;
            try
            {
                var consumerlistener = new TcpListener(IPAddress.Any, consumerPort);
                var nspApp = ServerContext.PortAppMap[consumerPort];

                consumerlistener.Start(1000);
                nspApp.Listener = consumerlistener;
                nspApp.CancelListenSource = cts;

                //临时编下号，以便在日志里区分不同隧道的连接
                string clientApp = $"clientapp:{nspApp.ClientId}-{nspApp.AppId}";
                while (!ct.IsCancellationRequested)
                {
                    Logger.Debug("listening serviceClient....Port:" + consumerPort);
                    //I.主要对外侦听循环
                    //Task.Factory.StartNew(consumerlistener.AcceptTcpClientAsync())

                    var consumerClient = await consumerlistener.AcceptTcpClientAsync();
                    ProcessConsumeRequestAsync(consumerPort, clientApp, consumerClient, ct);
                }
            }
            catch (ObjectDisposedException ode)
            {
                Logger.Debug($"外网端口{consumerPort}侦听时被外部终止");
            }
            catch (Exception ex)
            {
                Logger.Debug($"外网端口{consumerPort}侦听时出错{ex}");
            }
        }

        private async Task ProcessConsumeRequestAsync(int consumerPort, string clientApp, TcpClient consumerClient, CancellationToken ct)
        {
            TcpTunnel tunnel = new TcpTunnel();
            tunnel.ConsumerClient = consumerClient;
            ServerContext.PortAppMap[consumerPort].Tunnels.Add(tunnel);
            Logger.Debug("consumer已连接：" + consumerClient.Client.RemoteEndPoint.ToString());

            //II.弹出先前已经准备好的socket
            TcpClient s2pClient = await ConnectionManager.GetClient(consumerPort);

            tunnel.ClientServerClient = s2pClient;
            //✳关键过程✳
            //III.发送一个字节过去促使客户端建立转发隧道，至此隧道已打通
            //客户端接收到此消息后，会另外分配一个备用连接
            s2pClient.GetStream().WriteAndFlushAsync(new byte[] { 0x01 }, 0, 1);

            await TcpTransferAsync(consumerClient, s2pClient, clientApp, ct);
        }

        #region 配置连接相关
        //配置服务，客户端可以通过这个服务接收现有的空闲端口
        //accept a config request.
        //request:
        //   2          1       1
        //  clientid    appid   nouse
        //
        //response:
        //   2          1       1  ...N
        //  clientid    appid   port
        private async Task AcceptConfigRequest(TcpListener listenerConfigService)
        {
            listenerConfigService.Start(100);
            while (true)
            {
                var client = await listenerConfigService.AcceptTcpClientAsync();
                ProcessConfigRequestAsync(client);
            }
        }

        private async Task ProcessConfigRequestAsync(TcpClient client)
        {
            try
            {
#if DEBUG
                Server.Logger.Debug("config request received.");
#endif
                var nstream = client.GetStream();

                //0.读取协议名
                int protoRequestLength = 1;
                byte[] protoRequestBytes = new byte[protoRequestLength];

                int resultByte0 = await nstream.ReadAsync(protoRequestBytes);
                Protocol proto = (Protocol)protoRequestBytes[0];
#if DEBUG
                Server.Logger.Debug("appRequestBytes received.");
#endif
                if (resultByte0 == 0)
                {
                    CloseClient(client);
                    return;
                }

                switch (proto)
                {
                    case Protocol.ClientNewAppRequest:
                        await ProcessAppRequestProtocol(client);
                        break;
                    case Protocol.Heartbeat:
                        await ProcessHeartbeatProtocol(client);
                        break;
                    case Protocol.CloseClient:
                        await ProcessCloseClientProtocol(client);
                        break;
                    case Protocol.Reconnect:
                        await ProcessAppRequestProtocol(client, true);
                        break;
                    default:
                        throw new Exception("接收到异常请求。");
                }

            }
            catch (Exception e)
            {
                Logger.Debug(e);
                throw;
            }

        }

        private async Task ProcessCloseClientProtocol(TcpClient client)
        {
            Server.Logger.Debug("Now processing CloseClient protocol....");
            NetworkStream nstream = client.GetStream();
            int closeClientLength = 2;
            byte[] appRequestBytes = new byte[closeClientLength];
            int resultByte = await nstream.ReadAsync(appRequestBytes);
            //Server.Logger.Debug("appRequestBytes received.");
            if (resultByte == 0)
            {
                CloseClient(client);
                return;
            }

            int clientID = StringUtil.DoubleBytesToInt(appRequestBytes[0], appRequestBytes[1]);
            //2.更新最后更新时间
            ServerContext.CloseAllSourceByClient(clientID);
            //3.接收完立即关闭
            client.Close();
        }

        private async Task ProcessHeartbeatProtocol(TcpClient client)
        {
            //1.读取clientID

            NetworkStream nstream = client.GetStream();
            int heartBeatLength = 2;
            byte[] appRequestBytes = new byte[heartBeatLength];
            int resultByte = await nstream.ReadAsync(appRequestBytes);
            //Server.Logger.Debug("appRequestBytes received.");
            if (resultByte == 0)
            {
                CloseClient(client);
                return;
            }
            //1.2 响应ACK 
            await nstream.WriteAndFlushAsync(new byte[] { 0x01 }, 0, 1);
            int clientID = StringUtil.DoubleBytesToInt(appRequestBytes[0], appRequestBytes[1]);
#if DEBUG
            Server.Logger.Debug($"Now processing {clientID}'s Heartbeat protocol....");
#endif
            //2.更新最后更新时间
            if (ServerContext.Clients.ContainsKey(clientID))
            {
                ServerContext.Clients[clientID].LastUpdateTime = DateTime.Now;
            }
            else
            {
                Server.Logger.Debug($"clientId为{clientID}客户端已经被清除。");
            }

            //3.接收完立即关闭
            client.Close();
        }

        private async Task<bool> ProcessAppRequestProtocol(TcpClient client, bool IsReconnect = false)
        {
            Server.Logger.Debug("Now processing request protocol....");
            NetworkStream nstream = client.GetStream();
            int clientIdFromToken = 0;

            //1.读取配置请求1
            //如果是重连请求，则读取接下来5个字符，清
            //空服务端所有与该client相关的所有连接配置

            //TODO !!!!兼容原有的重连逻辑

            //TODO !!!!获取Token，截取clientID，校验
            //TODO !!!!这里的校验逻辑和httpserver_api存在太多重复，需要重构
            clientIdFromToken = await GetClientIdFromNextTokenBytes(client);
            //var userClaims = StringUtil.ConvertStringToTokenClaims(clientIdFromToken);
            if (clientIdFromToken == 0)
            {
                client.Close();
                return false;
            }

            //if (IsReconnect) 因为加入了始终校验的机制，取消重连规则
            //{
            ServerContext.CloseAllSourceByClient(clientIdFromToken);
            // }

            //1.3 获取客户端请求数
            int configRequestLength = 3;
            byte[] appRequestBytes = new byte[configRequestLength];
            int resultByte = await nstream.ReadAsync(appRequestBytes);
            Server.Logger.Debug("appRequestBytes received.");
            if (resultByte == 0)
            {
                CloseClient(client);
                return true;
            }

            //2.根据配置请求1获取更多配置信息
            int appCount = (int)appRequestBytes[2];
            byte[] consumerPortBytes = new byte[appCount * 2];
            int resultByte2 = await nstream.ReadAsync(consumerPortBytes);
            Server.Logger.Debug("consumerPortBytes received.");
            if (resultByte2 == 0)
            {
                CloseClient(client);
                return true;
            }

            //NSPClient nspClient;
            //3.分配配置ID，并且写回给客户端
            try
            {
                byte[] arrangedIds = ConnectionManager.ArrangeConfigIds(appRequestBytes, consumerPortBytes, clientIdFromToken);
                Server.Logger.Debug("apprequest arranged");
                await nstream.WriteAsync(arrangedIds);
            }
            catch (Exception ex)
            {
                Logger.Debug(ex.ToString());
            }
            finally
            {
                client.Close();
            }

            ////4.给NSPClient关联configclient
            //nspClient.LastUpdateTime
            Logger.Debug("arrangedIds written.");

            return false;
        }

        /// <summary>
        /// 通过token获取clientid
        /// 返回0说明失败
        /// </summary>
        /// <param name="client"></param>
        /// <returns></returns>
        private async Task<int> GetClientIdFromNextTokenBytes(TcpClient client)
        {
            NetworkStream nstream = client.GetStream();
            int clientIdFromToken = 0;
            //1.1 获取token长度
            int tokenLengthLength = 2;
            byte[] tokenLengthBytes = new byte[tokenLengthLength];
            int resultByte01 = await nstream.ReadAsync(tokenLengthBytes);
            Server.Logger.Debug("tokenLengthBytes received.");
            if (resultByte01 == 0)
            {
                CloseClient(client);
                return 0;
            }

            //1.2 获取token
            int tokenLength = StringUtil.DoubleBytesToInt(tokenLengthBytes);
            byte[] tokenBytes = new byte[tokenLength];
            int resultByte02 = await nstream.ReadAsync(tokenBytes);
            Server.Logger.Debug("tokenBytes received.");
            if (resultByte02 == 0)
            {
                CloseClient(client);
                return 0;
            }

            string token = tokenBytes.ToASCIIString();
            if (token != Global.NO_TOKEN_STRING)
            {
                var tokenClaims = StringUtil.ConvertStringToTokenClaims(token);
                var userJson = DbOp.Get(tokenClaims.UserKey);
                if (userJson == null)
                {
                    Server.Logger.Debug("token验证失败");
                }
                else
                {
                    var userId = userJson.ToObject<User>().userId;
                    if (ServerContext.ServerConfig.BoundConfig.UsersBanlist.Contains(userId))
                    {
                        Server.Logger.Debug("用户被禁用");
                        return 0;
                    }
                    else
                    {
                        clientIdFromToken = int.Parse(userId);
                    }
                }
            }

            return clientIdFromToken;
        }

        #endregion

        #region datatransfer
        //3端互相传输数据
        async Task TcpTransferAsync(TcpClient consumerClient, TcpClient providerClient,
            string clientApp,
            CancellationToken ct)
        {
            try
            {
                Server.Logger.Debug($"New client ({clientApp}) connected");

                CancellationTokenSource transfering = new CancellationTokenSource();

                var providerStream = providerClient.GetStream();
                var consumerStream = consumerClient.GetStream();
                Task taskC2PLooping = ToStaticTransfer(transfering.Token, consumerStream, providerStream, clientApp);
                Task taskP2CLooping = StreamTransfer(transfering.Token, providerStream, consumerStream, clientApp);

                //任何一端传输中断或者故障，则关闭所有连接
                var comletedTask = await Task.WhenAny(taskC2PLooping, taskP2CLooping);
                //comletedTask.
                Logger.Debug($"Transferring ({clientApp}) STOPPED");
                consumerClient.Close();
                providerClient.Close();
                transfering.Cancel();
            }
            catch (Exception e)
            {
                Logger.Debug(e);
                throw;
            }

        }

        private async Task StreamTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string clientApp)
        {
            using (fromStream)
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await fromStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
                {
                    await toStream.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                }
            }
            Server.Logger.Debug($"{clientApp}对服务端传输关闭。");
        }

        private async Task ToStaticTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string clientApp)
        {
            using (fromStream)
            {
                byte[] buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await fromStream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) != 0)
                {
                    await toStream.WriteAsync(buffer, 0, bytesRead, ct).ConfigureAwait(false);
                }
            }
            Server.Logger.Debug($"{clientApp}对客户端传输关闭。");
        }

        private void CloseClient(TcpClient client)
        {
            Logger.Debug("invalid request,Closing client:" + client.Client.RemoteEndPoint.ToString());
            client.Close();
            Logger.Debug("Closed client:" + client.Client.RemoteEndPoint.ToString());
        }



        #endregion


    }
}