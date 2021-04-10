using System;
using System.Data.Common;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MySql.Data.MySqlClient;

namespace VkYourCountryGameBackend
{
    class Program
    {
        private static bool stopped = false;
        private static HttpListener httpListener;
        private static string sqlConnectStr;
        static void Main(string[] args)
        {
            Console.WriteLine("starting server...");
            sqlConnectStr = "server=192.168.1.5;user=yourcountrygame_server;database=yourcountrygame;password=GameServerPasswordForSQL;";


            httpListener = new HttpListener();
            httpListener.Prefixes.Add("http://*:8080/yourcountryserver/");
            httpListener.Start();

            Console.WriteLine("server started");

            StartListening();
        }

        private static void StartListening()
        {
            while (!stopped)
            {
                HttpListenerContext context = httpListener.GetContext();
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    Task.Run(() => ProcessCORS(context));
                }
                else
                {
                    switch (context.Request.RawUrl)
                    {
                        case "/yourcountryserver/getUser":
                            Task.Run(()=>ProcessGetUser(context)); 
                            break;
                        case "/yourcountryserver/setUser":
                            Task.Run(() => ProcessSetUser(context));
                            break;
                        default:
                            Task.Run(() => Process404(context));
                            break;
                    }
                }
            }
        }

        private static async Task SendJson(HttpListenerContext context, JObject json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.ToString());
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        private static async Task ProcessCORS(HttpListenerContext context)
        {
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = 0;
            await context.Response.OutputStream.WriteAsync(new byte[0], 0, 0);

        }
        private static async Task Process404(HttpListenerContext context)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentLength64 = 0;
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            await context.Response.OutputStream.WriteAsync(new byte[0], 0, 0);

        }

        private static async Task ProcessSetUser(HttpListenerContext context)
        {
        }

        private static async Task ProcessGetUser(HttpListenerContext context)
        {
            try
            {
                string requestStr = await new StreamReader(context.Request.InputStream, Encoding.UTF8).ReadToEndAsync();
                JObject request = JObject.Parse(requestStr);

                MySqlConnection sqlConnection = new MySqlConnection(sqlConnectStr);
                await sqlConnection.OpenAsync();
                MySqlCommand command = new MySqlCommand($"SELECT * FROM user WHERE id = {request["id"]}", sqlConnection);
                DbDataReader reader = await command.ExecuteReaderAsync();
                bool found = await reader.ReadAsync();

                JObject json = new JObject();
                if (found)
                {
                    json.Add("money", reader.GetDouble(reader.GetOrdinal("money")));
                    json.Add("health", reader.GetByte(reader.GetOrdinal("health")));
                    json.Add("hunger", reader.GetByte(reader.GetOrdinal("hunger")));
                    json.Add("happiness", reader.GetByte(reader.GetOrdinal("happiness")));
                    if (!await reader.IsDBNullAsync(reader.GetOrdinal("owner_id")))
                        json.Add("owner", reader.GetInt32(reader.GetOrdinal("owner_id")));
                    json.Add("days", reader.GetInt32(reader.GetOrdinal("days")));
                }
                else
                {
                    json.Add("error", "userNotFound");
                }

                await reader.CloseAsync();

                await SendJson(context,json);

            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
            }
        }
    }
}
