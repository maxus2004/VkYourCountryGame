﻿using System;
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
        private static bool logging = true;
        private static HttpListener httpListener;
        private static string sqlConnectStr;
        static void Main(string[] args)
        {
            Console.WriteLine("starting server...");
            sqlConnectStr = "server=localhost;user=yourcountrygame_server;database=yourcountrygame;password=GameServerPasswordForSQL;";


            httpListener = new HttpListener();
            httpListener.Prefixes.Add("http://*:8080/yourcountryserver/");
            httpListener.Start();

            Console.WriteLine("server started");

            new Thread(StartListening).Start();

            while (!stopped)
            {
                string cmd = Console.ReadLine();

                switch (cmd)
                {
                    case "stop":
                    case "exit":
                    case "quit":
                        stopped = true;
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
                    default:
                        Console.WriteLine("Unknown command, write 'help'");
                        break;
                }
            }
        }

        private static void log(string str)
        {
            if (!logging) return;
            Console.WriteLine(str);
        }
        private static void StartListening()
        {
            while (!stopped)
            {
                HttpListenerContext context = httpListener.GetContext();
                if (context.Request.HttpMethod == "OPTIONS")
                {
                    Task.Run(() => SendCORSHeaders(context));
                }
                else
                {
                    switch (context.Request.RawUrl)
                    {
                        case "/yourcountryserver/getUser":
                            Task.Run(() => ProcessGetUser(context));
                            break;
                        case "/yourcountryserver/setUser":
                            Task.Run(() => ProcessSetUser(context));
                            break;
                        default:
                            Task.Run(() => Send404(context));
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
                DbDataReader getUserSql = await new MySqlCommand(
                    $"SELECT * FROM user WHERE id = `{request["id"]}`",
                    sqlConnection).ExecuteReaderAsync();
                bool found = await getUserSql.ReadAsync();

                JObject json = new JObject();
                if (found)
                {
                    json.Add("money", getUserSql.GetDouble(getUserSql.GetOrdinal("money")));
                    json.Add("health", getUserSql.GetByte(getUserSql.GetOrdinal("health")));
                    json.Add("hunger", getUserSql.GetByte(getUserSql.GetOrdinal("hunger")));
                    json.Add("happiness", getUserSql.GetByte(getUserSql.GetOrdinal("happiness")));
                    if (!await getUserSql.IsDBNullAsync(getUserSql.GetOrdinal("owner_id")))
                        json.Add("owner", getUserSql.GetInt32(getUserSql.GetOrdinal("owner_id")));
                    json.Add("days", getUserSql.GetInt32(getUserSql.GetOrdinal("days")));
                    await getUserSql.CloseAsync();
                }
                else
                {
                    await getUserSql.CloseAsync();

                    DbDataReader addUserSql = await new MySqlCommand(
                        "INSERT INTO user (id, money, health, hunger, happiness, owner_id, days) " +
                        $"VALUES (`{request["id"]}`, `0`, `100`, `100`, `100`, NULL, `0`)",
                        sqlConnection).ExecuteReaderAsync();
                    await addUserSql.CloseAsync();

                    log("added user " + request["id"]);

                    json.Add("money", 0);
                    json.Add("health", 100);
                    json.Add("hunger", 100);
                    json.Add("happiness", 100);
                    json.Add("days", 0);

                }


                await SendJson(context, json);
                log($"served getUser {request} for {context.Request.UserHostAddress}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
            }
        }
    }
}
