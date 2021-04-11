using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using Newtonsoft.Json.Linq;

namespace VkYourCountryGameBackend
{
    class GameTask
    {
        public GameTask(string name, long cost, long reward, int rewardDelay, bool repeating)
        {
            this.name = name;
            this.cost = cost;
            this.reward = reward;
            this.rewardDelay = rewardDelay;
            this.repeating = repeating;
        }

        public string name;
        public long cost;
        public long reward;
        public int rewardDelay;
        public bool repeating;
    }

    class Game
    {
        static GameTask[] tasks = {
            new (name: "Сдавать металлолом", cost: 0, reward: 50, rewardDelay: 0, repeating: false ),
            new (name: "Сдавать металлолом", cost: 0, reward: 50, rewardDelay: 0, repeating: false ),
            new (name: "Попрошайничать", cost: 0, reward: 50, rewardDelay: 0, repeating: false ),
            new (name: "Работать на складе", cost: 0, reward: 5000, rewardDelay: 3, repeating: false ),
            new (name: "Продавать мороженное", cost: 500, reward: 2000, rewardDelay: 0, repeating: false ),
            new (name: "Открыть магазин", cost: 50000, reward: 200000, rewardDelay: 30, repeating: true ),
            new (name: "Стать президентом", cost: 100000000, reward: 10000000000, rewardDelay: 365, repeating: false )
        };

        public static async Task ProcessDoTask(HttpListenerContext context, MySqlConnection sqlConnection)
        {
            try
            {
                if (!int.TryParse(context.Request.QueryString["vk_user_id"], out int userId))
                {
                    await Program.SendError(context, "invalid user id");
                    return;
                }
                if (!int.TryParse(context.Request.QueryString["taskId"], out int taskId))
                {
                    await Program.SendError(context, "invalid task id");
                    return;
                }
                if (taskId < 0 || taskId >= tasks.Length)
                {
                    await Program.SendError(context, "invalid task id");
                    return;
                }

                DbDataReader getUserSql = await new MySqlCommand(
                    $"SELECT * FROM user WHERE id = '{userId}'",
                    sqlConnection).ExecuteReaderAsync();
                await getUserSql.ReadAsync();

                long money = getUserSql.GetInt64(getUserSql.GetOrdinal("money"));
                byte health = getUserSql.GetByte(getUserSql.GetOrdinal("health"));
                byte hunger = getUserSql.GetByte(getUserSql.GetOrdinal("hunger"));
                byte happiness = getUserSql.GetByte(getUserSql.GetOrdinal("happiness"));
                int? owner;
                if (!await getUserSql.IsDBNullAsync(getUserSql.GetOrdinal("owner_id")))
                    owner = getUserSql.GetInt32(getUserSql.GetOrdinal("owner_id"));
                else
                    owner = null;
                int days = getUserSql.GetInt32(getUserSql.GetOrdinal("days")));
                await getUserSql.CloseAsync();

                if (owner == null)
                {
                    money -= tasks[taskId].cost;
                    money += tasks[taskId].reward;
                }
                else
                {
                    money -= tasks[taskId].cost;
                    money += tasks[taskId].reward/2;

                   long? ownerMoney = (long?)await new MySqlCommand(
                        $"SELECT money FROM user WHERE id = '{owner}'",
                        sqlConnection).ExecuteScalarAsync();
                   if (ownerMoney != null)
                   {
                       ownerMoney += tasks[taskId].reward / 2;
                       await new MySqlCommand(
                           $"UPDATE user SET money = '{ownerMoney}' WHERE id = '{owner}'",
                           sqlConnection).ExecuteNonQueryAsync();
                    }
                   else
                   {
                       ownerMoney = tasks[taskId].reward / 2;
                       await new MySqlCommand(
                           "INSERT INTO user (id, money, health, hunger, happiness, owner_id, days) " +
                           $"VALUES ('{owner}', '{ownerMoney}', '100', '100', '100', NULL, '0')",
                           sqlConnection).ExecuteNonQueryAsync();
                    }
                }


                await new MySqlCommand(
                    $"UPDATE user SET money = '{money}' WHERE id = '{userId}'",
                    sqlConnection).ExecuteNonQueryAsync();

                JObject json = new JObject();
                json.Add("money", money);
                json.Add("health", health);
                json.Add("hunger", hunger);
                json.Add("happiness", happiness);
                json.Add("owner", owner);
                json.Add("days", days);
                await Program.SendJson(context, json);

                Program.Log($"served doTask for id{context.Request.QueryString["vk_user_id"]}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
            }
        }

        public static async Task ProcessGetUser(HttpListenerContext context, MySqlConnection sqlConnection)
        {
            try
            {
                if (!int.TryParse(context.Request.QueryString["vk_user_id"], out int userId))
                {
                    await Program.SendError(context, "invalid user id");
                    return;
                }
                await sqlConnection.OpenAsync();
                DbDataReader getUserSql = await new MySqlCommand(
                    $"SELECT * FROM user WHERE id = '{userId}'",
                    sqlConnection).ExecuteReaderAsync();
                bool found = await getUserSql.ReadAsync();

                JObject json = new JObject();
                if (found)
                {
                    json.Add("money", getUserSql.GetInt64(getUserSql.GetOrdinal("money")));
                    json.Add("health", getUserSql.GetByte(getUserSql.GetOrdinal("health")));
                    json.Add("hunger", getUserSql.GetByte(getUserSql.GetOrdinal("hunger")));
                    json.Add("happiness", getUserSql.GetByte(getUserSql.GetOrdinal("happiness")));
                    if (!await getUserSql.IsDBNullAsync(getUserSql.GetOrdinal("owner_id")))
                        json.Add("owner", getUserSql.GetInt32(getUserSql.GetOrdinal("owner_id")));
                    else
                        json.Add("owner", null);
                    json.Add("days", getUserSql.GetInt32(getUserSql.GetOrdinal("days")));
                    await getUserSql.CloseAsync();
                }
                else
                {
                    await getUserSql.CloseAsync();

                    await new MySqlCommand(
                        "INSERT INTO user (id, money, health, hunger, happiness, owner_id, days) " +
                        $"VALUES ('{userId}', '0', '100', '100', '100', NULL, '0')",
                        sqlConnection).ExecuteNonQueryAsync();

                    Program.Log("added user " + userId);

                    json.Add("money", 0);
                    json.Add("health", 100);
                    json.Add("hunger", 100);
                    json.Add("happiness", 100);
                    json.Add("owner", null);
                    json.Add("days", 0);

                }

                await Program.SendJson(context, json);
                Program.Log($"served getUser for id{context.Request.QueryString["vk_user_id"]}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
            }
        }
    }
}
