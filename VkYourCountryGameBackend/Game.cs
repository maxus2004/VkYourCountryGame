using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VkYourCountryGameBackend
{
    struct GameTask
    {
        public string db_name;
        public long cost;
        public long reward;
        public int rewardInterval;
        public bool repeating;
        public double failRate;
    }

    struct PlayerData
    {
        public long money;
        public byte health;
        public byte hunger;
        public byte happiness;
        public int? owner;
        public int days;

    }

    class Game
    {
        private static Random random = new Random();

        static GameTask[] tasks = {
            new GameTask{ db_name = "scrap_metal", cost = 0,         reward = 50,          rewardInterval = 0,   repeating = false, failRate = 0.1},
            new GameTask{ db_name = "beg",         cost = 0,         reward = 50,          rewardInterval = 0,   repeating = false, failRate = 0.25},
            new GameTask{ db_name = "warehouse",   cost = 0,         reward = 5000,        rewardInterval = 0,   repeating = false, failRate = 0.1},
            new GameTask{ db_name = "ice_cream",   cost = 500,       reward = 2000,        rewardInterval = 0,   repeating = false, failRate = 0.05},
            new GameTask{ db_name = "shop",        cost = 50000,     reward = 200000,      rewardInterval = 30,  repeating = true,  failRate = 0.15},
            new GameTask{ db_name = "president",   cost = 100000000, reward = 10000000000, rewardInterval = 365, repeating = true,  failRate = 0.5}
        };

        private static async Task<PlayerData> DoPlayerTask(MySqlConnection sqlConnection, int taskId, PlayerData playerData)
        {

            if (playerData.owner == null)
            {
                playerData.money += tasks[taskId].reward;
            }
            else
            {
                playerData.money += tasks[taskId].reward / 2;

                long? ownerMoney = (long?)await new MySqlCommand(
                    $"SELECT money FROM user WHERE id = '{playerData.owner}'",
                    sqlConnection).ExecuteScalarAsync();
                if (ownerMoney != null)
                {
                    ownerMoney += tasks[taskId].reward / 2;
                    await new MySqlCommand(
                        $"UPDATE user SET money = '{ownerMoney}' WHERE id = '{playerData.owner}'",
                        sqlConnection).ExecuteNonQueryAsync();
                }
                else
                {
                    ownerMoney = tasks[taskId].reward / 2;
                    await new MySqlCommand(
                        "INSERT INTO user (id, money, health, hunger, happiness, owner_id, days) " +
                        $"VALUES ('{playerData.owner}', '{ownerMoney}', '100', '100', '100', NULL, '0')",
                        sqlConnection).ExecuteNonQueryAsync();
                }
            }


            return playerData;
        }
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

                await sqlConnection.OpenAsync();

                DbDataReader getUserSql = await new MySqlCommand(
                    $"SELECT * FROM user WHERE id = '{userId}'",
                    sqlConnection).ExecuteReaderAsync();
                await getUserSql.ReadAsync();

                PlayerData playerData = new PlayerData();

                playerData.money = getUserSql.GetInt64(getUserSql.GetOrdinal("money"));
                playerData.health = getUserSql.GetByte(getUserSql.GetOrdinal("health"));
                playerData.hunger = getUserSql.GetByte(getUserSql.GetOrdinal("hunger"));
                playerData.happiness = getUserSql.GetByte(getUserSql.GetOrdinal("happiness"));
                if (!await getUserSql.IsDBNullAsync(getUserSql.GetOrdinal("owner_id")))
                    playerData.owner = getUserSql.GetInt32(getUserSql.GetOrdinal("owner_id"));
                else
                    playerData.owner = null;
                playerData.days = getUserSql.GetInt32(getUserSql.GetOrdinal("days"));
                await getUserSql.CloseAsync();

                if (playerData.money < tasks[taskId].cost)
                {
                    await sqlConnection.CloseAsync();
                    await Program.SendError(context, "not enough money");
                    return;
                }

                playerData.money -= tasks[taskId].cost;
                playerData.days += 1;
                bool taskFailed = false;

                if (tasks[taskId].repeating == false)
                {
                    if (random.NextDouble() < tasks[taskId].failRate)
                        taskFailed = true;
                    else
                        playerData = await DoPlayerTask(sqlConnection, taskId, playerData);
                }
                else
                {
                    int? count = (int?)await new MySqlCommand(
                        $"SELECT COUNT(*) FROM user_tasks WHERE user_id = '{userId}' AND task_name = '{tasks[taskId].db_name}'",
                        sqlConnection).ExecuteScalarAsync();
                    if (count == 0)
                    {
                        await new MySqlCommand(
                            "INSERT INTO user_tasks (task_name, user_id, started_at) " +
                            $"VALUES ('{tasks[taskId].db_name}', '{userId}', '{playerData.days}')",
                            sqlConnection).ExecuteNonQueryAsync();
                    }
                    else
                    {
                        await Program.SendError(context, $"task {taskId} already active");
                        return;
                    }
                }

                await new MySqlCommand(
                    $"UPDATE user SET money = '{playerData.money}', days = '{playerData.days}' WHERE id = '{userId}'",
                    sqlConnection).ExecuteNonQueryAsync();

                JObject json = new JObject();
                json.Add("playerData", JsonConvert.SerializeObject(playerData));
                json.Add("failed", taskFailed);

                await sqlConnection.CloseAsync();
                await Program.SendJson(context, json);
                Program.Log($"served doTask {taskId} for id{userId}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
                await sqlConnection.CloseAsync();
            }
        }

        public static async Task ProcessCancelTask(HttpListenerContext context, MySqlConnection sqlConnection)
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

                await sqlConnection.OpenAsync();
                await new MySqlCommand(
                    "DELETE FROM user_tasks " +
                    $"WHERE task_name = '{tasks[taskId].db_name}' AND user_id = '{userId}')",
                    sqlConnection).ExecuteNonQueryAsync();
                await sqlConnection.CloseAsync();

                Program.Log($"served cancelTask {taskId} for id{userId}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
                await sqlConnection.CloseAsync();
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

                await sqlConnection.CloseAsync();
                await Program.SendJson(context, json);
                Program.Log($"served getUser for id{userId}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
                await sqlConnection.CloseAsync();
            }
        }
    }
}
