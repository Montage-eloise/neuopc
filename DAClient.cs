﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Collections;
using OPCAutomation;
using System.Net;
using System.Linq;
using System.Threading;
using Serilog;
using System.Threading.Channels;

namespace neuopc
{
    public delegate void ItemsReset(List<Item> items);
    public delegate void ValueUpdate(List<Item> items);

    public class Node
    {
        public string Name { get; set; }
        public DaType Type { get; set; }
        public OPCItem Item { get; set; }
    }

    public enum DaType
    {
        Int8 = 16, // UA_TYPES_SBYTE/VT_I1
        Int16 = 2, // UA_TYPES_INT16/VT_I2
        Int32B = 22, // UA_TYPES_INT32/VT_INT 
        Int32 = 3, // UA_TYPES_INT32/VT_I4
        Int64 = 20, // UA_TYPES_INT64/VT_I8
        Float = 4, // UA_TYPES_FLOAT/VT_R4
        Double = 5, // UA_TYPES_DOUBLE/VT_R8
        UInt8 = 17, // UA_TYPES_BYTE/VT_UI1
        UInt16 = 18, // UA_TYPES_UINT16/VT_UI2
        UInt32B = 23, // UA_TYPES_UINT32/VT_UINT
        UInt32 = 19, // UA_TYPES_UINT32/VT_UI4
        UInt64 = 21, // UA_TYPES_UINT64/VT_UI8
        Date = 7, // UA_TYPES_DATETIME/VT_DATE
        String = 8, // UA_TYPES_STRING/VT_BSTR
        Bool = 11, // UA_TYPES_BOOLEAN/VT_BOOL
        Money = 6, // Money
    }

    public enum DaRights
    {
        Read = 1,
        Write = 2,
        Rw = 3,
    }

    public enum DaQuality
    {
        Good = 192,
        Bad = 0,
    }

    public enum MsgType
    {
        List = 0,
        Data = 1,
    }

    public class Item
    {
        public string Name { get; set; }
        public DaType Type { get; set; }
        public DaRights Rights { get; set; }
        public int ClientHandle { get; set; }
        public dynamic Value { get; set; }
        public DaQuality Quality { get; set; }
        public DateTime Timestamp { get; set; }
        public int Error { get; set; }
    }

    public class DaMsg
    {
        public MsgType Type { get; set; }
        public List<Item> Items { get; set; }
    }

    public class DaClient
    {
        private const int MaxRead = 50;
        private OPCServer server;
        private OPCBrowser brower;
        private OPCGroups groups;
        private OPCGroup group;
        private List<Node> nodes;
        private string hostName;
        private string serverName;

        private Thread thread;
        private bool running;
        private object locker;

        public ValueUpdate Update;
        public ItemsReset Reset;
        private List<Channel<DaMsg>> channels;

        public DaClient()
        {
            running = false;
            locker = new object();
            nodes = new List<Node>();
            channels = new List<Channel<DaMsg>>();
        }


        public static List<string> GetHosts()
        {
            var list = new List<string>();
            var ipHost = Dns.GetHostEntry("127.0.0.1");
            list.Add(ipHost.HostName);
            // TODO: enum all host
            return list;
        }

        public static List<string> GetServers(string host)
        {
            var list = new List<string>();
            try
            {
                var opcServer = new OPCServer();
                object servers = opcServer.GetOPCServers(host);
                list.AddRange(((Array)servers).Cast<string>());
            }
            catch (Exception exception)
            {
                Log.Warning($"get opc servers failed, error:{exception.Message}");
            }

            return list;
        }

        public void AddDaMsgChannel(Channel<DaMsg> channel)
        {
            if (null == channel) { return; }
            channels.Add(channel);
        }

        private bool SetServer()
        {
            if (Connected())
            {
                Disconnect();
            }

            try
            {
                server = new OPCServer();
                server.Connect(serverName, hostName);
            }
            catch (Exception exception)
            {
                server = null;
                Log.Error($"connect to {hostName}/{serverName} failed, error:{exception.Message}");
                return false;
            }

            Log.Information($"Server {hostName}/{serverName} connected");
            return true;
        }

        private bool SetBrower()
        {
            if (null == server) { return false; }

            try
            {
                brower = server.CreateBrowser();
                if (null == brower)
                {
                    return false;
                }

                brower.ShowBranches();
                brower.ShowLeafs(true);
            }
            catch (Exception error)
            {
                brower = null;
                Log.Error($"create browser failed, msg:{error.Message}");
                return false;
            }

            return true;
        }

        private bool SetGroup()
        {
            if (null == server) { return false; }

            try
            {
                groups = server.OPCGroups;
                groups.DefaultGroupIsActive = true;
                group = groups.Add("all");
                group.IsActive = true;
            }
            catch (Exception error)
            {
                groups = null;
                group = null;
                Log.Error($"add group failed, msg:{error.Message}");
                return false;
            }

            return true;
        }

        private bool SetNodes()
        {
            if (null == server) { return false; }
            if (null == groups) { return false; }
            if (null == group) { return false; }
            if (null == brower) { return false; }

            nodes.Clear();
            int index = 0;
            foreach (var item in brower)
            {
                var node = new Node()
                {
                    Name = item.ToString(),
                };

                try
                {
                    node.Item = group.OPCItems.AddItem(node.Name, index);
                    node.Type = (DaType)node.Item.CanonicalDataType;
                    nodes.Add(node);
                    index++;
                }
                catch (Exception exception)
                {
                    Log.Warning($"add item failed, name:{item}, error:{exception.Message}");
                }

                Log.Information($"add item secceed, name:{item}, type:{node.Type}");
            }

            return true;
        }

        private void SetItems()
        {
            var items = (from node in nodes
                        let name = node.Name
                        select new Item
                        {
                            Name = name,
                            Type = (DaType)node.Item.CanonicalDataType,
                            Rights = (DaRights)node.Item.AccessRights,
                            ClientHandle = node.Item.ClientHandle,
                            Quality = 0,
                            Timestamp = DateTime.Now,
                        }).ToList();

            //Reset?.Invoke(list);

            foreach (var channel in channels)
            {
                var msg = new DaMsg
                {
                    Type = MsgType.List,
                    Items = items,
                };
                channel.Writer.TryWrite(msg);
            }
        }

        private bool SetChange()
        {
            if (null == groups) { return false; }
            if (null == group) { return false; }

            groups.DefaultGroupDeadband = 0;
            groups.DefaultGroupUpdateRate = 200;

            group.IsSubscribed = true;
            group.UpdateRate = 200;
            group.DataChange += GroupDataChange;

            return true;
        }

        private bool Connect()
        {
            bool ret = false;
            ret = SetServer();
            ret = SetBrower();
            ret = SetGroup();
            ret = SetNodes();
            SetItems();
            ret = SetChange();
            return ret;
        }

        private bool Connected()
        {
            if (null == server) { return false; }
            return 0 == server.ServerState;
        }

        private bool Read()
        {
            int count = null == nodes ? 0 : nodes.Count;
            int t1 = count / MaxRead;
            int t2 = (count % MaxRead) == 0 ? 0 : 1;
            int times = t1 + t2;
            for (int i = 0; i < times; i++)
            {
                var tempNodes = nodes.Skip(i * MaxRead).Take(MaxRead).ToList();
                var list = tempNodes.Select(node => node.Item.ServerHandle).ToList();
                list.Insert(0, 0);
                Array handles = list.ToArray();
                var items = new List<Item>();

                Array values = null;
                Array errors = null;
                dynamic qualities = null;
                dynamic timestamps = null;

                try
                {
                    group.SyncRead(1, tempNodes.Count, ref handles, out values, out errors, out qualities, out timestamps);
                }
                catch (Exception exception)
                {
                    Log.Error($"sync read group error: {exception.Message}");
                    return false;
                }

                for (int j = 0; j < tempNodes.Count; j++)
                {
                    var n = tempNodes[j];
                    var item = new Item
                    {
                        Name = n.Name,
                        ClientHandle = n.Item.ClientHandle,
                        Type = n.Type,
                        Value = values.GetValue(j + 1),
                        Rights = (DaRights)n.Item.AccessRights,
                        Quality = (DaQuality)Convert.ToInt32(((Array)qualities).GetValue(j + 1)),
                        Error = Convert.ToInt32(errors.GetValue(j + 1)),
                        Timestamp = Convert.ToDateTime(((Array)timestamps).GetValue(j + 1)).ToLocalTime(),
                    };
                    items.Add(item);
                }

                //Update?.Invoke(items);

                foreach (var channel in channels)
                {
                    var msg = new DaMsg
                    {
                        Type = MsgType.Data,
                        Items = items,
                    };
                    channel.Writer.TryWrite(msg);
                }
            }

            return true;
        }

        private void Disconnect()
        {
            try
            {
                group.DataChange -= GroupDataChange;
                server?.Disconnect();
            }
            catch (Exception exception)
            {
                Log.Error($"disconnect error:{exception.Message}");
            }
        }

        private void GroupDataChange(int transactionId, int numItems, ref Array clientHandles, ref Array itemValues, ref Array qualities, ref Array timeStamps)
        {
            var items = new List<Item>();
            for (var i = 1; i <= numItems; i++)
            {
                var handle = Convert.ToInt32(clientHandles.GetValue(i));
                var node = nodes.FirstOrDefault(n => n.Item.ClientHandle == handle);
                if (null == node)
                {
                    continue;
                }

                var item = new Item
                {
                    Name = node.Name,
                    ClientHandle = handle,
                    Type = node.Type,
                    Value = itemValues.GetValue(i),
                    Rights = (DaRights)node.Item.AccessRights,
                    Quality = (DaQuality)Convert.ToInt32(qualities.GetValue(i)),
                    Timestamp = Convert.ToDateTime(timeStamps.GetValue(i)).ToLocalTime(),
                };

                items.Add(item);
            }

            //Update?.Invoke(items);
            foreach (var channel in channels)
            {
                var msg = new DaMsg
                {
                    Type = MsgType.Data,
                    Items = items,
                };
                channel.Writer.TryWrite(msg);
            }
        }

        public bool Write(Item item)
        {
            var writeNode = nodes?.FirstOrDefault(node => node.Name == item.Name);
            if (null != writeNode)
            {
                try
                {
                    writeNode.Item.Write(item.Value);
                }
                catch (Exception exception)
                {
                    Log.Error($"write node failed, name:{writeNode.Name}, value:{item.Value}, error:{exception.Message}");
                    return false;
                }

                Log.Information($"write node succeed, name:{writeNode.Name}, value:{item.Value}");
                return true;
            }
            else
            {
                Log.Error($"write node failed, name:{item.Name}, value:{item.Value}");
                return false;
            }
        }

        private void Refresh()
        {
            // First connect
            bool ret = false;
            ret = Connect();
            while (running)
            {
                ret = Read();
                if (!ret)
                {
                    ret = Connect();
                }

                Thread.Sleep(100);
            }

            Disconnect();
        }

        public void Open(string host, string name)
        {
            hostName = host;
            serverName = name;

            if (running)
            {
                if (null != thread)
                {
                    running = false;
                }
            }

            running = true;
            var ts = new ThreadStart(Refresh);
            thread = new Thread(ts);
            thread.Start();
        }

        public void Close()
        {
            running = false;
            thread?.Join();
        }
    }
}
