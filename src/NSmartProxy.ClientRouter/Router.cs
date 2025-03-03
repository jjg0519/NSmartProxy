﻿using NSmartProxy.Data;
using NSmartProxy.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NSmartProxy.Shared;
using System.IO;
using NSmartProxy.ClientRouter.Dispatchers;
using NSmartProxy.Data.Models;

namespace NSmartProxy.Client
{
    public class NullLogger : INSmartLogger
    {
        public void Debug(object message)
        {
            //Not Implemented
        }

        public void Error(object message, Exception ex)
        {
            //Not Implemented
        }

        public void Info(object message)
        {
            //Not Implemented
        }
    }

    public enum ClientStatus
    {
        Stopped = 0,
        Started = 1,
        LoginError = 2
    }

    public class Router
    {
        public const string NSMART_CLIENT_CACHE_PATH = "./cli_cache_v2.cache";
        CancellationTokenSource ONE_LIVE_TOKEN_SRC;
        CancellationTokenSource CANCEL_TOKEN_SRC;
        CancellationTokenSource TRANSFERING_TOKEN_SRC;
        CancellationTokenSource HEARTBEAT_TOKEN_SRC;
        TaskCompletionSource<object> _waiter;

        public ServerConnectionManager ConnectionManager;
        public bool IsStarted = false;

        public Action DoServerNoResponse = delegate { };
        public Action<ClientStatus, List<string>> StatusChanged = delegate { };

        internal NSPClientConfig ClientConfig;
        internal LoginInfo CurrentLoginInfo;

        internal static INSmartLogger Logger = new NullLogger();   //inject
        internal static Guid TimeStamp; //时间戳，用来标识对象是否已经发生变化

        public Router()
        {
            ONE_LIVE_TOKEN_SRC = new CancellationTokenSource();
        }

        public Router(INSmartLogger logger) : this()
        {
            Logger = logger;
        }

        public Router SetConfiguration(NSPClientConfig config)
        {
            ClientConfig = config;
            return this;
        }

        public Router SetLoginInfo(LoginInfo loginInfo)
        {
            this.CurrentLoginInfo = loginInfo;
            return this;
        }

        /// <summary>
        /// 重要：连接服务端，一般做为入口方法
        /// 该方法主要操作一些配置和心跳
        /// AlwaysReconnect：始终重试，开启此选项，无论何时，一旦程序在连接不上时都会进行重试，否则只在连接成功后的异常中断时才重试。
        /// </summary>
        /// <returns></returns>
        public async Task Start(bool AlwaysReconnect = false)
        {
            if (AlwaysReconnect) IsStarted = true;
            var oneLiveToken = ONE_LIVE_TOKEN_SRC.Token;
            //登录功能
            string arrangedToken = Global.NO_TOKEN_STRING;

            while (!oneLiveToken.IsCancellationRequested)
            {
                CANCEL_TOKEN_SRC = new CancellationTokenSource();
                TRANSFERING_TOKEN_SRC = new CancellationTokenSource();
                HEARTBEAT_TOKEN_SRC = new CancellationTokenSource();
                _waiter = new TaskCompletionSource<object>();
                Router.TimeStamp = Guid.NewGuid();

                var appIdIpPortConfig = ClientConfig.Clients;
                int clientId = 0;

                //0.5 处理登录/重登录/匿名登录逻辑
                try
                {
                    //登录
                    if (CurrentLoginInfo != null)
                    {
                        var loginResult = await Login();
                        arrangedToken = loginResult.Item1;
                        clientId = loginResult.Item2;
                    }
                    else if (File.Exists(NSMART_CLIENT_CACHE_PATH))
                    { //登录缓存

                        arrangedToken = File.ReadAllText(NSMART_CLIENT_CACHE_PATH);
                        //TODO 这个token的合法性无法保证,如果服务端删除了用户，而这里缓存还存在，会导致无法登录
                        //TODO ***** 这是个trick：防止匿名用户被服务端踢了之后无限申请新账号
                        CurrentLoginInfo = null;
                    }
                    else
                    {
                        //匿名登录，未提供登录信息时，使用空用户名密码自动注册并尝试匿名登录
                        Router.Logger.Debug("未提供登录信息，尝试匿名登录");
                        CurrentLoginInfo = new LoginInfo() { UserName = "", UserPwd = "" };
                        var loginResult = await Login();
                        arrangedToken = loginResult.Item1;
                        clientId = loginResult.Item2;
                        //保存缓存到磁盘
                        File.WriteAllText(NSMART_CLIENT_CACHE_PATH, arrangedToken);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("启动失败：" + ex.Message, ex);
                    return;
                }
                //1.获取配置
                ConnectionManager = ServerConnectionManager.Create(clientId);
                ConnectionManager.CurrentToken = arrangedToken;
                ConnectionManager.ClientGroupConnected += ServerConnnectionManager_ClientGroupConnected;
                ConnectionManager.ServerNoResponse = DoServerNoResponse;//下钻事件
                ClientModel clientModel = null;//
                try
                {
                    //非第一次则算作重连，发送clientid过去
                    clientModel = await ConnectionManager.InitConfig(this.ClientConfig).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    //TODO 状态码：连接失败
                    Router.Logger.Error("连接失败：" + ex.Message, ex);
                    //throw;
                }

                //HasConnected = true;
                if (clientModel != null)
                {
                    int counter = 0;

                    //1.5 写入缓存
                    //File.WriteAllBytes(NSMART_CLIENT_CACHE_PATH, StringUtil.IntTo2Bytes(clientModel.ClientId));
                    //2.分配配置：appid为0时说明没有分配appid，所以需要分配一个
                    foreach (var app in appIdIpPortConfig)
                    {
                        if (app.AppId == 0)
                        {
                            app.AppId = clientModel.AppList[counter].AppId;
                            counter++;
                        }
                    }
                    Logger.Debug("****************port list*************");
                    List<string> tunnelstrs = new List<string>();
                    foreach (var ap in clientModel.AppList)
                    {
                        var cApp = appIdIpPortConfig.First(obj => obj.AppId == ap.AppId);
                        var tunnelStr = ap.AppId.ToString() + ":  " + ClientConfig.ProviderAddress + ":" +
                                        ap.Port.ToString() + "=>" +
                                        cApp.IP + ":" + cApp.TargetServicePort;
                        Logger.Debug(tunnelStr);
                        tunnelstrs.Add(tunnelStr);
                    }
                    Logger.Debug("**************************************");
                    ConnectionManager.PollingToProvider(StatusChanged, tunnelstrs);
                    //3.创建心跳连接
                    ConnectionManager.StartHeartBeats(Global.HeartbeatInterval, HEARTBEAT_TOKEN_SRC.Token, _waiter);

                    IsStarted = true;
                    Exception exception = await _waiter.Task.ConfigureAwait(false) as Exception;
                    if (exception != null)
                        Router.Logger.Debug($"程序异常终止:{exception.Message}。");
                    else Router.Logger.Debug($"未知异常。");
                }
                else
                {
                    Router.Logger.Debug($"程序启动失败。");
                    //如果程序从未启动过就出错，则终止程序，否则重试。
                    if (IsStarted == false) { StatusChanged(ClientStatus.Stopped, null); return; }
                }

                Router.Logger.Debug($"连接故障，尝试关闭连接并重试");
                if (ConnectionManager != null)
                    ConnectionManager.CloseAllConnections();//关闭所有连接
                //出错重试
                await Task.Delay(Global.ClientReconnectInterval, ONE_LIVE_TOKEN_SRC.Token);
                //TODO 返回错误码
                //await Task.Delay(TimeSpan.FromHours(24), CANCEL_TOKEN.CurrentToken).ConfigureAwait(false);
                Router.Logger.Debug($"连接关闭，开启重试");
            }
            //正常终止
            Router.Logger.Debug($"停止重试，循环终止。");
        }

        private async Task<ValueTuple<string, int>> Login()
        {
            string arrangedToken;
            int clientId;
            NSPDispatcher disp = new NSPDispatcher($"{ClientConfig.ProviderAddress}:{ClientConfig.ProviderWebPort}");
            var result = await disp.LoginFromClient(CurrentLoginInfo.UserName ?? "", CurrentLoginInfo.UserPwd ?? "");
            if (result.State == 1)
            {
                Router.Logger.Debug("登录成功");
                var data = result.Data;
                arrangedToken = data.Token;
                Router.Logger.Debug($"服务端版本号：{data.Version},当前适配版本号{Global.NSmartProxyServerName}");
                clientId = int.Parse(data.Userid);
                File.WriteAllText(NSMART_CLIENT_CACHE_PATH, arrangedToken);
            }
            else
            {
                StatusChanged(ClientStatus.LoginError, null);
                throw new Exception("登录失败，服务端返回错误如下：" + result.Msg);
            }

            return (arrangedToken, clientId);
        }

        private void ServerConnnectionManager_ClientGroupConnected(object sender, EventArgs e)
        {
            var args = (ClientGroupEventArgs)e;
            foreach (TcpClient providerClient in args.NewClients)
            {
                Router.Logger.Debug("Open server connection.");
                OpenTrasferation(args.App.AppId, providerClient);
            }

        }

        /// <summary>
        /// 彻底关闭客户端并且不再重试
        /// </summary>
        /// <returns></returns>
        public async Task Close()
        {
            try
            {
                var config = ClientConfig;
                //客户端关闭
                CANCEL_TOKEN_SRC.Cancel();
                TRANSFERING_TOKEN_SRC.Cancel();
                HEARTBEAT_TOKEN_SRC.Cancel();
                ONE_LIVE_TOKEN_SRC.Cancel();
                _waiter.SetCanceled();
                //服务端关闭
                await NetworkUtil.ConnectAndSend(
                        config.ProviderAddress,
                    config.ProviderConfigPort,
                        Protocol.CloseClient,
                        StringUtil.IntTo2Bytes(this.ConnectionManager.ClientID),
                        true)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Router.Logger.Debug("关闭失败！" + ex);
            }
        }

        private async Task OpenTrasferation(int appId, TcpClient providerClient)
        {
            TcpClient toTargetServer = new TcpClient();
            //事件循环2
            try
            {
                byte[] buffer = new byte[1];
                NetworkStream providerClientStream = providerClient.GetStream();
                //接收首条消息，首条消息中返回的是appid和客户端
                //消费端长连接，需要在server端保活
                try
                {
                    int readByteCount = await providerClientStream.ReadAsync(buffer, 0, buffer.Length);
                    if (readByteCount == 0)
                    {
                        //抛出错误以便上层重启客户端。
                        _waiter.TrySetResult(new Exception($"连接{appId}被服务器主动切断，已断开连接"));
                        return;

                    }
                }
                catch (Exception ex)
                {
                    //反弹连接出错为致命错误
                    //此处出错后，应用程序需要重置，并重启
                    _waiter.TrySetResult(ex);
                    throw;
                }

                //连接后设置client为null 
                if (ConnectionManager.ExistClient(appId, providerClient))
                {
                    var removedClient = ConnectionManager.RemoveClient(appId, providerClient);
                    if (removedClient == false)
                    {
                        Router.Logger.Debug($"没有移除{appId}任何的对象，对象不存在. hash:{providerClient.GetHashCode()}");
                        return;
                    }
                }
                else
                {
                    Router.Logger.Debug($"已无法在{appId}中找到客户端 hash:{providerClient.GetHashCode()}.");
                    return;
                }
                //每移除一个链接则发起一个新的链接
                Router.Logger.Debug(appId + "接收到连接请求");
                //根据clientid_appid发送到固定的端口
                //TODO 序列没有匹配元素？
                ClientApp item = ClientConfig.Clients.First((obj) => obj.AppId == appId);

                //向服务端发起一次长连接，没有接收任何外来连接请求时，
                //该方法会在write处会阻塞。
                await ConnectionManager.ConnectAppToServer(appId);
                Router.Logger.Debug("已建立反向连接:" + appId);
                // item1:app编号，item2:ip地址，item3:目标服务端口
                try
                {
                    toTargetServer.Connect(item.IP, item.TargetServicePort);
                }
                catch
                {
                    throw new Exception($"对内网服务的 {item.IP}：{item.TargetServicePort} 连接失败。");
                }
                string epString = item.IP.ToString() + ":" + item.TargetServicePort.ToString();
                Router.Logger.Debug("已连接目标服务:" + epString);

                NetworkStream targetServerStream = toTargetServer.GetStream();
                //targetServerStream.Write(buffer, 0, readByteCount);
                TcpTransferAsync(providerClientStream, targetServerStream, providerClient, toTargetServer, epString);
                //already close connection

            }
            catch (Exception ex)
            {
                Logger.Debug("传输时出错：" + ex);
                //关闭传输连接，服务端也会相应处理，把0request发送给消费端
                //TODO ***: 连接时出错，重启客户端
                toTargetServer.Close();
                providerClient.Close();
                throw;
            }

        }


        private async Task TcpTransferAsync(NetworkStream providerStream, NetworkStream targetServceStream, TcpClient providerClient, TcpClient toTargetServer, string epString)
        {
            try
            {
                Router.Logger.Debug("Looping start.");
                //创建相互转发流
                var taskT2PLooping = ToStaticTransfer(TRANSFERING_TOKEN_SRC.Token, targetServceStream, providerStream, epString);
                var taskP2TLooping = StreamTransfer(TRANSFERING_TOKEN_SRC.Token, providerStream, targetServceStream, epString);

                //close connnection,whether client or server stopped transferring.
                var comletedTask = await Task.WhenAny(taskT2PLooping, taskP2TLooping);
                //Router.Logger.Debug(comletedTask.Result + "传输关闭，重新读取字节");
                providerClient.Close();
                Router.Logger.Debug("已关闭toProvider连接。");
                toTargetServer.Close();
                Router.Logger.Debug("已关闭toTargetServer连接。");
            }
            catch (Exception ex)
            {
                Router.Logger.Debug(ex.ToString());
            }
        }



        private async Task StreamTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string epString)
        {
            using (fromStream)
            {
                await fromStream.CopyToAsync(toStream, 4096, ct);
            }
            Router.Logger.Debug($"{epString}对节点传输关闭。");


        }


        private async Task ToStaticTransfer(CancellationToken ct, NetworkStream fromStream, NetworkStream toStream, string epString)
        {
            using (fromStream)
            {
                await fromStream.CopyToAsync(toStream, 4096, ct);
            }
            Router.Logger.Debug($"{epString}反向链接传输关闭。");
        }

        private void SendZero(int port)
        {
            TcpClient tc = new TcpClient();
            tc.Connect("127.0.0.1", port);
            tc.Client.Send(new byte[] { 0x00 });
        }
    }

}
