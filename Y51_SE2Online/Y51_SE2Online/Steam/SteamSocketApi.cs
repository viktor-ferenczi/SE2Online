using System;
using Steamworks;

namespace Y51_SE2Online.Steam;

internal sealed class SteamSocketApi
{
    private readonly bool _server;
    public SteamSocketApi(bool server) => _server = server;
    public ESteamNetworkingAvailability InitAuthentication() => _server ? SteamGameServerNetworkingSockets.InitAuthentication() : SteamNetworkingSockets.InitAuthentication();
    public HSteamNetPollGroup CreatePollGroup() => _server ? SteamGameServerNetworkingSockets.CreatePollGroup() : SteamNetworkingSockets.CreatePollGroup();
    public bool DestroyPollGroup(HSteamNetPollGroup group) => _server ? SteamGameServerNetworkingSockets.DestroyPollGroup(group) : SteamNetworkingSockets.DestroyPollGroup(group);
    public HSteamListenSocket CreateListenSocketP2P(int port, int count, SteamNetworkingConfigValue_t[] options) => _server ? SteamGameServerNetworkingSockets.CreateListenSocketP2P(port, count, options) : SteamNetworkingSockets.CreateListenSocketP2P(port, count, options);
    public HSteamListenSocket CreateListenSocketIP(ref SteamNetworkingIPAddr address, int count, SteamNetworkingConfigValue_t[] options) => _server ? SteamGameServerNetworkingSockets.CreateListenSocketIP(ref address, count, options) : SteamNetworkingSockets.CreateListenSocketIP(ref address, count, options);
    public HSteamNetConnection ConnectP2P(ref SteamNetworkingIdentity identity, int port, int count, SteamNetworkingConfigValue_t[] options) => _server ? SteamGameServerNetworkingSockets.ConnectP2P(ref identity, port, count, options) : SteamNetworkingSockets.ConnectP2P(ref identity, port, count, options);
    public HSteamNetConnection ConnectByIPAddress(ref SteamNetworkingIPAddr address, int count, SteamNetworkingConfigValue_t[] options) => _server ? SteamGameServerNetworkingSockets.ConnectByIPAddress(ref address, count, options) : SteamNetworkingSockets.ConnectByIPAddress(ref address, count, options);
    public EResult AcceptConnection(HSteamNetConnection connection) => _server ? SteamGameServerNetworkingSockets.AcceptConnection(connection) : SteamNetworkingSockets.AcceptConnection(connection);
    public bool CloseConnection(HSteamNetConnection connection, int code, string reason, bool bEnableLinger) => _server ? SteamGameServerNetworkingSockets.CloseConnection(connection, code, reason, bEnableLinger) : SteamNetworkingSockets.CloseConnection(connection, code, reason, bEnableLinger);
    public bool CloseListenSocket(HSteamListenSocket socket) => _server ? SteamGameServerNetworkingSockets.CloseListenSocket(socket) : SteamNetworkingSockets.CloseListenSocket(socket);
    public bool SetConnectionPollGroup(HSteamNetConnection connection, HSteamNetPollGroup group) => _server ? SteamGameServerNetworkingSockets.SetConnectionPollGroup(connection, group) : SteamNetworkingSockets.SetConnectionPollGroup(connection, group);
    public bool GetConnectionInfo(HSteamNetConnection connection, out SteamNetConnectionInfo_t info) => _server ? SteamGameServerNetworkingSockets.GetConnectionInfo(connection, out info) : SteamNetworkingSockets.GetConnectionInfo(connection, out info);
    public int ReceiveMessagesOnPollGroup(HSteamNetPollGroup group, IntPtr[] messages, int count) => _server ? SteamGameServerNetworkingSockets.ReceiveMessagesOnPollGroup(group, messages, count) : SteamNetworkingSockets.ReceiveMessagesOnPollGroup(group, messages, count);
    public EResult ConfigureConnectionLanes(HSteamNetConnection connection, int count, int[] priorities, ushort[] weights) => _server ? SteamGameServerNetworkingSockets.ConfigureConnectionLanes(connection, count, priorities, weights) : SteamNetworkingSockets.ConfigureConnectionLanes(connection, count, priorities, weights);
    public void SendMessages(int count, IntPtr[] messages, long[] results) { if (_server) SteamGameServerNetworkingSockets.SendMessages(count, messages, results); else SteamNetworkingSockets.SendMessages(count, messages, results); }
    public EResult GetConnectionRealTimeStatus(HSteamNetConnection connection, ref SteamNetConnectionRealTimeStatus_t status, int lanes, ref SteamNetConnectionRealTimeLaneStatus_t laneStatus) => _server ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, lanes, ref laneStatus) : SteamNetworkingSockets.GetConnectionRealTimeStatus(connection, ref status, lanes, ref laneStatus);
    public int GetDetailedConnectionStatus(HSteamNetConnection connection, out string detail, int capacity) => _server ? SteamGameServerNetworkingSockets.GetDetailedConnectionStatus(connection, out detail, capacity) : SteamNetworkingSockets.GetDetailedConnectionStatus(connection, out detail, capacity);
    public IntPtr AllocateMessage(int size) => _server ? SteamGameServerNetworkingUtils.AllocateMessage(size) : SteamNetworkingUtils.AllocateMessage(size);
    public bool SetConfigValue(ESteamNetworkingConfigValue key, ESteamNetworkingConfigScope scope, IntPtr obj, ESteamNetworkingConfigDataType type, IntPtr value) => _server ? SteamGameServerNetworkingUtils.SetConfigValue(key, scope, obj, type, value) : SteamNetworkingUtils.SetConfigValue(key, scope, obj, type, value);
}
