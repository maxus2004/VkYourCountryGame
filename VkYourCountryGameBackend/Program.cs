using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using MySql.Data.MySqlClient;
namespace VkYourCountryGameBackend
{
    class Program
    {
        private static string secretKey = "EI8EGdM3svbzs76k8HYG";
        private static bool stopped = false;
        private static bool logging = true;
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

            new Thread(StartListening).Start();

            Thread.Sleep(1000);
            while (!stopped)
            {
                Console.Write("> ");
                string cmd = Console.ReadLine();

                switch (cmd)
                {
                    case "stop":
                    case "exit":
                    case "quit":
                        Console.WriteLine("stopping server...");
                        stopped = true;
                        httpListener.Stop();
                        break;
                    case "start logging":
                        logging = true;
                        break;
                    case "stop logging":
                        logging = false;
                        break;
                    case "help":
                        Console.WriteLine("available commands: \n" +
                                          "help\n" +
                                          "stop/exit/quit\n" +
                                          "start logging\n" +
                                          "stop logging");
                        break;
                    case "":
                        break;
                    default:
                        Console.WriteLine("Unknown command, write 'help'");
                        break;
                }
            }
        }

        public static void Log(string str)
        {
            if (!logging) return;
            Console.Write("\r");
            Console.WriteLine(DateTime.Now.ToString("dd.mm.yyyy HH:mm:ss") + " - " + str);
            Console.Write("> ");
        }
        private static void StartListening()
        {
            while (!stopped)
            {
                HttpListenerContext context;
                try
                {
                    context = httpListener.GetContext();
                }
                catch (HttpListenerException e)
                {
                    if (e.ErrorCode != 500)
                    {
                        Console.Error.WriteLine(e);
                    }
                    continue;
                }

                if (context.Request.HttpMethod == "OPTIONS")
                {
                    Task.Run(() => SendCORSHeaders(context));
                }
                else
                {
                    if (!VerifyUser(context.Request.QueryString))
                    {
                        if (context.Request.QueryString.AllKeys.Contains("vk_user_id"))
                            Log($"unauthorized client tried to connect as id{context.Request.QueryString["vk_user_id"]}");
                        else
                            Log($"unauthorized client tried to connect");
                        Task.Run(() => SendError(context, "unauthorized"));
                    }
                    else if (context.Request.Url is not null)
                    {
                        switch (context.Request.Url.LocalPath)
                        {
                            case "/yourcountryserver/getUser":
                                Task.Run(() => Game.ProcessGetUser(context, new MySqlConnection(sqlConnectStr)));
                                break;
                            case "/yourcountryserver/doTask":
                                Task.Run(() => Game.ProcessDoTask(context, new MySqlConnection(sqlConnectStr)));
                                break;
                            case "/yourcountryserver/cancelTask":
                                Task.Run(() => Game.ProcessCancelTask(context, new MySqlConnection(sqlConnectStr)));
                                break;
                            case "/yourcountryserver/getFree":
                                Task.Run(() => Game.ProcessGetFree(context, new MySqlConnection(sqlConnectStr)));
                                break;
                            case "/yourcountryserver/becomeSlave":
                                Task.Run(() => Game.ProcessBecomeSlave(context, new MySqlConnection(sqlConnectStr)));
                                break;
                            case "/yourcountryserver/getLeaders":
                                Task.Run(() => Game.ProcessGetLeaders(context, new MySqlConnection(sqlConnectStr)));
                                break;
                            default:
                                Task.Run(() => Send404(context));
                                break;
                        }
                    }
                }
            }
            Console.WriteLine("stopped server");
        }

        private static bool VerifyUser(NameValueCollection query)
        {
            SortedDictionary<string, string> vkKeys = new SortedDictionary<string, string>();
            foreach (string name in query.AllKeys)
            {
                if (name != null && name.StartsWith("vk_"))
                {
                    vkKeys.Add(name, query[name]);
                }
            }

            string str = "";
            foreach (KeyValuePair<string, string> pair in vkKeys)
            {
                str += pair.Key + "=" + pair.Value + "&";
            }
            str = str.TrimEnd('&').Replace(",", "%2C");
            string sign = Convert.ToBase64String(new HMACSHA256(Encoding.UTF8.GetBytes(secretKey)).ComputeHash(Encoding.UTF8.GetBytes(str)));

            //Console.WriteLine(str);
            //Console.WriteLine(sign);
            //Console.WriteLine(query["sign"]);

            return sign.TrimEnd('=').Replace('+', '-').Replace('/', '_') == query["sign"];
        }
        public static async Task SendJson(HttpListenerContext context, JObject json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.ToString());
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        public static async Task SendError(HttpListenerContext context, string error)
        {
            JObject json = new JObject();
            json.Add("error", error);
            await SendJson(context, json);
        }

        private static async Task SendCORSHeaders(HttpListenerContext context)
        {
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            context.Response.StatusCode = 200;
            context.Response.ContentLength64 = 0;
            await context.Response.OutputStream.WriteAsync(new byte[0], 0, 0);

        }
        private static async Task Send404(HttpListenerContext context)
        {
            context.Response.StatusCode = 404;
            context.Response.ContentLength64 = 0;
            context.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            await context.Response.OutputStream.WriteAsync(new byte[0], 0, 0);

        }
    }
}
