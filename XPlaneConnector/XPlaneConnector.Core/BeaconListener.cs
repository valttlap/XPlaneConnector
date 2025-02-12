using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XPlaneConnector.Core
{
    public static class BeaconListener
    {
        private const int BeaconPort = 49707;
        private const string MulticastGroupAddress = "239.255.1.1";

        /// <summary>
        /// Listens on UDP multicast for the first valid X-Plane beacon packet and returns the discovered address and port.
        /// </summary>
        /// <param name="token">Optional cancellation token to abort listening.</param>
        /// <returns>Tuple of (IPAddress, port) if found, otherwise (IPAddress.None, 0).</returns>
        public static async Task<IPEndPoint> GetXPlaneClientAddressAsync(CancellationToken token = default)
        {
            using var client = new UdpClient();
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            IPEndPoint localEp = new(IPAddress.Any, BeaconPort);
            client.Client.Bind(localEp);

            IPAddress multicastAddress = IPAddress.Parse(MulticastGroupAddress);
            client.JoinMulticastGroup(multicastAddress);

            while (!token.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await client.ReceiveAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // If canceled, just exit the loop and return (IPAddress.None, 0).
                    break;
                }
                catch (SocketException)
                {
                    break;
                }

                byte[] data = result.Buffer;

                // 1) Check if packet is at least the minimum length to read "BECN\0", data[15], and data[19].
                if (data.Length < 20)
                    continue;

                // 2) Check if the first 5 bytes match "BECN\0"
                if (!Encoding.ASCII.GetString(data, 0, 5)
                                  .Equals("BECN\0", StringComparison.Ordinal))
                {
                    continue;
                }

                if (data[15] != 1)
                    continue;

                // 3) Now read the port from data[19..21]
                ushort port = BitConverter.ToUInt16(data, 19);

                return new(result.RemoteEndPoint.Address, port);
            }

            // If canceled or no beacon found, return default
            return new(IPAddress.None, 0);
        }
    }
}

