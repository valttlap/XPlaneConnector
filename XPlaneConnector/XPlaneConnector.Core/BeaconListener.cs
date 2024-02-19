using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XPlaneConnector.Core;

public class BeaconListener
{
    private const int BeaconPort = 49707;
    private const string MulticastGroupAddress = "239.255.1.1";

    public static async Task<(IPAddress Address, int Port)> GetXPlaneClientAddressAsync()
    {
        using var client = new UdpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        IPEndPoint localEp = new(IPAddress.Any, BeaconPort);
        client.Client.Bind(localEp);

        IPAddress multicastAddress = IPAddress.Parse(MulticastGroupAddress);
        client.JoinMulticastGroup(multicastAddress);

        while (true)
        {
            var result = await client.ReceiveAsync();
            byte[] data = result.Buffer;

            if (!Encoding.ASCII.GetString(data, 0, 5).Equals("BECN\0")) continue;

            if (data[15] != 1) continue;

            ushort port = BitConverter.ToUInt16(data, 19);

            return (result.RemoteEndPoint.Address, port);
        }
    }
}
