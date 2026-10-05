using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;

namespace SprocketMP.Net
{
    /// <summary>
    /// Minimal star-topology transport. On the host every remote peer gets a positive connection id.
    /// On a client there is exactly one connection: id 0 = the host.
    /// </summary>
    public interface ITransport
    {
        bool IsServer { get; }
        string Name { get; }
        string Status { get; }
        void Poll();
        void Send(int conn, byte[] data, bool reliable);
        void Disconnect(int conn);
        void Shutdown();
        IEnumerable<int> Connections { get; }
        int Ping(int conn);

        event Action<int> Connected;
        event Action<int, string> Disconnected;
        event Action<int, byte[]> Received;
    }

    /// <summary>Direct IP / LAN transport based on LiteNetLib (UDP, reliable + unreliable channels).</summary>
    public sealed class LnlTransport : ITransport, INetEventListener
    {
        const string Key = "SprocketMP-v1";
        readonly NetManager _net;
        readonly Dictionary<int, NetPeer> _peers = new Dictionary<int, NetPeer>();
        public bool IsServer { get; }
        public string Name => "IP";
        public string Status { get; private set; } = "";
        public IEnumerable<int> Connections => _peers.Keys;

        public event Action<int> Connected;
        public event Action<int, string> Disconnected;
        public event Action<int, byte[]> Received;

        LnlTransport(bool server)
        {
            IsServer = server;
            _net = new NetManager(this)
            {
                AutoRecycle = true,
                UnconnectedMessagesEnabled = false,
                DisconnectTimeout = 15000,
                UpdateTime = 10,
                ChannelsCount = 2,
                IPv6Enabled = false,
            };
        }

        public static LnlTransport Host(int port)
        {
            var t = new LnlTransport(true);
            if (!t._net.Start(port)) throw new Exception("Не удалось открыть UDP порт " + port + " (занят?)");
            t.Status = "Сервер слушает UDP порт " + port;
            return t;
        }

        public static LnlTransport Join(string address, int port)
        {
            var t = new LnlTransport(false);
            if (!t._net.Start()) throw new Exception("Не удалось открыть сокет");
            t._net.Connect(address, port, Key);
            t.Status = "Подключение к " + address + ":" + port + "...";
            return t;
        }

        public void Poll() => _net.PollEvents();

        public void Send(int conn, byte[] data, bool reliable)
        {
            if (!_peers.TryGetValue(conn, out var p)) return;
            p.Send(data, reliable ? (byte)0 : (byte)1, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
        }

        public void Disconnect(int conn)
        {
            if (_peers.TryGetValue(conn, out var p)) p.Disconnect();
        }

        public void Shutdown() => _net.Stop(true);

        public int Ping(int conn) => _peers.TryGetValue(conn, out var p) ? p.Ping : -1;

        int IdOf(NetPeer peer) => IsServer ? peer.Id + 1 : 0;

        public void OnPeerConnected(NetPeer peer)
        {
            int id = IdOf(peer);
            _peers[id] = peer;
            if (!IsServer) Status = "Подключено к " + peer.ToString();
            Connected?.Invoke(id);
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            int id = IdOf(peer);
            _peers.Remove(id);
            if (!IsServer) Status = "Отключено: " + info.Reason;
            Disconnected?.Invoke(id, info.Reason.ToString());
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Plugin.Log.LogWarning("Network error " + endPoint + ": " + socketError);
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            var data = reader.GetRemainingBytes();
            Received?.Invoke(IdOf(peer), data);
        }

        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }

        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        public void OnConnectionRequest(ConnectionRequest request)
        {
            if (IsServer) request.AcceptIfKey(Key); else request.Reject();
        }
    }
}
