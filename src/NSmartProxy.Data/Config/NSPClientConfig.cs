﻿using NSmartProxy.Data;
using System;
using System.Collections.Generic;
using System.Text;

namespace NSmartProxy.Data
{ 
    public class NSPClientConfig
    {
        public int ProviderPort;                    //代理转发服务端口
        public int ProviderConfigPort;              //配置服务端口
        public string ProviderAddress;              //代理服务器地址
        public int ProviderWebPort = 12309;                    //web管理端的端口，默认12309 //TODO 暂时写死，以后再改
        public List<ClientApp> Clients = new List<ClientApp>();//客户端app
    }
}
