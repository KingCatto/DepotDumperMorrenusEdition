using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
namespace DepotDumper
{
    class HttpClientFactory
    {
        public static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new SocketsHttpHandler
            {
                ConnectCallback = IPv4ConnectAsync
            });
            var assemblyVersion = typeof(HttpClientFactory).Assembly.GetName().Version.ToString(fieldCount: 3);
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DepotDumper", assemblyVersion));
            return client;
        }
        static async ValueTask<Stream> IPv4ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
