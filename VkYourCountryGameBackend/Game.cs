using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Net;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
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
        public int slaves;
    }

    class Game
    {
        private static Random random = new Random();

        private static Dictionary<string, int> taskIds = new()
        {
            { "watch_ad" , 0},
            { "scrap_metal", 1 },
            { "beg", 2 },
            { "warehouse", 3 },
            { "ice_cream", 4 },
            { "pc_master", 5 },
            { "shop", 6 },
            { "president", 7 }
        };

        static GameTask[] tasks = {
            new GameTask{ db_name = "watch_ad",    cost = 0,         reward = 0,           rewardInterval = 0,   repeating = false, failRate = 0},
            new GameTask{ db_name = "scrap_metal", cost = 0,         reward = 50,          rewardInterval = 0,   repeating = false, failRate = 0.1},
            new GameTask{ db_name = "beg",         cost = 0,         reward = 50,          rewardInterval = 0,   repeating = false, failRate = 0.3},
            new GameTask{ db_name = "warehouse",   cost = 250,       reward = 1500,        rewardInterval = 0,   repeating = false, failRate = 0.2},
            new GameTask{ db_name = "ice_cream",   cost = 500,       reward = 1500,        rewardInterval = 0,   repeating = false, failRate = 0.05},
            new GameTask{ db_name = "pc_master",   cost = 4000,      reward = 15000,       rewardInterval = 0,   repeating = false, failRate = 0.1},
            new GameTask{ db_name = "shop",        cost = 100000,    reward = 200000,      rewardInterval = 10,  repeating = true,  failRate = 0.3},
            new GameTask{ db_name = "president",   cost = 100000000, reward = 100000000,   rewardInterval = 20, repeating = true,  failRate = 0.5}
        };

        private static async Task<PlayerData> DoPlayerTask(MySqlConnection sqlConnection, int taskId, PlayerData playerData)
        {
            long reward = tasks[taskId].reward;
            if (taskId == 0) reward = (long)(playerData.money * 0.1);
            if (playerData.owner == null)
            {
                playerData.money += reward;
            }
            else
            {
                playerData.money += reward / 2;

                long? ownerMoney = (long?)await new MySqlCommand(
                    $"SELECT money FROM user WHERE id = '{playerData.owner}'",
                    sqlConnection).ExecuteScalarAsync();
                if (ownerMoney != null)
                {
                    ownerMoney += reward / 2;
                    await new MySqlCommand(
                        $"UPDATE user SET money = '{ownerMoney}' WHERE id = '{playerData.owner}'",
                        sqlConnection).ExecuteNonQueryAsync();
                }
                else
                {
                    ownerMoney = reward / 2;
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
                playerData.slaves = (int)((long?) await new MySqlCommand(
                    $"SELECT COUNT(*) FROM user WHERE owner_id = '{userId}'",
                    sqlConnection).ExecuteScalarAsync() ?? 0);
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
                    long? count = (long?)await new MySqlCommand(
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

                DbDataReader getUserTasksSql = await new MySqlCommand(
                    $"SELECT * FROM user_tasks WHERE user_id = '{userId}'",
                    sqlConnection).ExecuteReaderAsync();
                List<int> tasksToDo = new List<int>();
                while (await getUserTasksSql.ReadAsync())
                {
                    string taskName = getUserTasksSql.GetString(getUserTasksSql.GetOrdinal("task_name"));
                    int startedAt = getUserTasksSql.GetInt32(getUserTasksSql.GetOrdinal("started_at"));
                    int repeatingTaskId = taskIds[taskName];

                    if ((playerData.days - startedAt) > 0 && (playerData.days - startedAt) % tasks[repeatingTaskId].rewardInterval == 0)
                    {
                        tasksToDo.Add(repeatingTaskId);
                    }
                }
                await getUserTasksSql.CloseAsync();

                foreach (int repeatingTaskId in tasksToDo)
                {
                    playerData = await DoPlayerTask(sqlConnection, repeatingTaskId, playerData);
                    Program.Log($"player id{userId} earned from job {repeatingTaskId}");
                }

                await new MySqlCommand(
                    $"UPDATE user SET money = '{playerData.money}', days = '{playerData.days}' WHERE id = '{userId}'",
                    sqlConnection).ExecuteNonQueryAsync();

                JObject json = new JObject();
                json.Add("playerData", JObject.FromObject(playerData));
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
                    $"WHERE task_name = '{tasks[taskId].db_name}' AND user_id = '{userId}'",
                    sqlConnection).ExecuteNonQueryAsync();
                await sqlConnection.CloseAsync();

                JObject json = new JObject { { "result", "ok" } };
                await Program.SendJson(context, json);
                Program.Log($"served cancelTask {taskId} for id{userId}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
                await sqlConnection.CloseAsync();
            }
        }

        public static async Task ProcessGetLeaders(HttpListenerContext context, MySqlConnection sqlConnection)
        {
            try
            {
                if (!int.TryParse(context.Request.QueryString["vk_user_id"], out int userId))
                {
                    await Program.SendError(context, "invalid user id");
                    return;
                }

                await sqlConnection.OpenAsync();

                JObject leaders = new JObject();

                DbDataReader getLeadersSql = await new MySqlCommand(
                    "SELECT * FROM `user` ORDER BY money DESC",
                    sqlConnection).ExecuteReaderAsync();

                for (int i = 0; i < 20; i++)
                {
                    leaders[i] = new JObject
                    {
                        { "id", getLeadersSql.GetInt64(getLeadersSql.GetOrdinal("money")) },
                        { "money", getLeadersSql.GetInt64(getLeadersSql.GetOrdinal("money")) },
                        { "money", getLeadersSql.GetInt64(getLeadersSql.GetOrdinal("money")) },
                    };
                }

                await sqlConnection.CloseAsync();

                await Program.SendJson(context, leaders);
                Program.Log($"served getLeaders for id{userId}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
                await sqlConnection.CloseAsync();
            }
        }

        public static async Task ProcessGetFree(HttpListenerContext context, MySqlConnection sqlConnection)
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
                playerData.slaves = (int)((long?)await new MySqlCommand(
                    $"SELECT COUNT(*) FROM user WHERE owner_id = '{userId}'",
                    sqlConnection).ExecuteScalarAsync() ?? 0);

                if (playerData.owner == null)
                {
                    await sqlConnection.CloseAsync();
                    await Program.SendError(context, "already free");
                    return;
                }

                if (playerData.money < 1000000)
                {
                    await sqlConnection.CloseAsync();
                    await Program.SendError(context, "not enough money");
                    return;
                }

                long? ownerMoney = (long?)await new MySqlCommand(
                    $"SELECT money FROM user WHERE id = '{playerData.owner}'",
                    sqlConnection).ExecuteScalarAsync();

                ownerMoney += 1000000;

                await new MySqlCommand(
                    $"UPDATE user SET money = '{ownerMoney}' WHERE id = '{playerData.owner}'",
                    sqlConnection).ExecuteNonQueryAsync();

                playerData.owner = null;
                playerData.money -= 1000000;

                await new MySqlCommand(
                    $"UPDATE user SET owner_id = NULL, money = '{playerData.money}' WHERE id = '{userId}'",
                    sqlConnection).ExecuteNonQueryAsync();

                await sqlConnection.CloseAsync();

                JObject json = JObject.FromObject(playerData);
                await Program.SendJson(context, json);
                Program.Log($"served getFree for id{userId}");
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                context.Response.OutputStream.Close();
                await sqlConnection.CloseAsync();
            }
        }

        public static async Task ProcessBecomeSlave(HttpListenerContext context, MySqlConnection sqlConnection)
        {
            try
            {
                if (!int.TryParse(context.Request.QueryString["vk_user_id"], out int userId))
                {
                    await Program.SendError(context, "invalid user id");
                    return;
                }
                if (!int.TryParse(context.Request.QueryString["owner_id"], out int ownerId))
                {
                    await Program.SendError(context, "invalid owner id");
                    return;
                }

                await sqlConnection.OpenAsync();
                bool isNull = Convert.IsDBNull(await new MySqlCommand(
                    $"SELECT owner_id FROM user WHERE id = '{userId}'",
                    sqlConnection).ExecuteScalarAsync());

                if (!isNull)
                {
                    await sqlConnection.CloseAsync();
                    await Program.SendError(context, "already slave");
                    return;
                }
                await new MySqlCommand(
                    $"UPDATE user SET owner_id = '{ownerId}' WHERE id = '{userId}'",
                    sqlConnection).ExecuteNonQueryAsync();
                await sqlConnection.CloseAsync();

                JObject json = new JObject{{"owner",ownerId}};
                await Program.SendJson(context, json);
                Program.Log($"served become slave of id{ownerId} for id{userId}");
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

                JObject userJson = new JObject();
                if (found)
                {
                    userJson.Add("money", getUserSql.GetInt64(getUserSql.GetOrdinal("money")));
                    userJson.Add("health", getUserSql.GetByte(getUserSql.GetOrdinal("health")));
                    userJson.Add("hunger", getUserSql.GetByte(getUserSql.GetOrdinal("hunger")));
                    userJson.Add("happiness", getUserSql.GetByte(getUserSql.GetOrdinal("happiness")));
                    if (!await getUserSql.IsDBNullAsync(getUserSql.GetOrdinal("owner_id")))
                        userJson.Add("owner", getUserSql.GetInt32(getUserSql.GetOrdinal("owner_id")));
                    else
                        userJson.Add("owner", null);
                    userJson.Add("days", getUserSql.GetInt32(getUserSql.GetOrdinal("days")));
                    await getUserSql.CloseAsync();
                    userJson.Add("slaves", (long?)await new MySqlCommand(
                        $"SELECT COUNT(*) FROM user WHERE owner_id = '{userId}'",
                        sqlConnection).ExecuteScalarAsync());
                }
                else
                {
                    await getUserSql.CloseAsync();

                    await new MySqlCommand(
                        "INSERT INTO user (id, money, health, hunger, happiness, owner_id, days) " +
                        $"VALUES ('{userId}', '0', '100', '100', '100', NULL, '0')",
                        sqlConnection).ExecuteNonQueryAsync();

                    Program.Log("added user " + userId);

                    userJson.Add("money", 0);
                    userJson.Add("health", 100);
                    userJson.Add("hunger", 100);
                    userJson.Add("happiness", 100);
                    userJson.Add("owner", null);
                    userJson.Add("days", 0);
                    userJson.Add("slaves", 0);
                }

                DbDataReader getUserTasksSql = await new MySqlCommand(
                    $"SELECT task_name FROM user_tasks WHERE user_id = '{userId}'",
                    sqlConnection).ExecuteReaderAsync();
                JArray tasksJson = new JArray();
                while (await getUserTasksSql.ReadAsync())
                {
                    string taskName = getUserTasksSql.GetString(0);
                    tasksJson.Add(JToken.FromObject(taskIds[taskName]));
                }
                await getUserTasksSql.CloseAsync();

                JObject json = new JObject
                {
                    {"playerData", userJson}, 
                    {"activeTasks", tasksJson}
                };

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
