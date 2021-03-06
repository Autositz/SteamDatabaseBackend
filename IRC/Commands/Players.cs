﻿/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using Dapper;
using SteamKit2;

namespace SteamDatabaseBackend
{
    class PlayersCommand : Command
    {
        public PlayersCommand()
        {
            Trigger = "players";

            Steam.Instance.CallbackManager.Register(new Callback<SteamUserStats.NumberOfPlayersCallback>(OnNumberOfPlayers));
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Length == 0)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} players <appid or partial game name>", Colors.OLIVE);
                CommandHandler.ReplyToCommand(command, true, "Use {0}^{1} and {2}${3} just like in regex to narrow down your match, e.g:{4} !players Portal$", Colors.BLUE, Colors.NORMAL, Colors.BLUE, Colors.NORMAL, Colors.OLIVE);

                return;
            }

            uint appID;

            if (!uint.TryParse(command.Message, out appID))
            {
                string name = command.Message;

                if (!Utils.ConvertUserInputToSQLSearch(ref name))
                {
                    CommandHandler.ReplyToCommand(command, "Your request is invalid or too short.");

                    return;
                }

                using (var db = Database.GetConnection())
                {
                    appID = db.ExecuteScalar<uint>("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE (`AppsTypes`.`Name` IN ('game', 'application') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name)) OR (`AppsTypes`.`Name` = 'unknown' AND `Apps`.`LastKnownName` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new { Name = name });
                }

                if (appID == 0)
                {
                    CommandHandler.ReplyToCommand(command, "Nothing was found matching your request.");

                    return;
                }
            }

            JobManager.AddJob(
                () => Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID),
                new JobManager.IRCRequest
                {
                    Target = appID,
                    Command = command
                }
            );
        }

        private static void OnNumberOfPlayers(SteamUserStats.NumberOfPlayersCallback callback)
        {
            JobAction job;

            if (!JobManager.TryRemoveJob(callback.JobID, out job) || !job.IsCommand)
            {
                return;
            }

            var request = job.CommandRequest;

            if (callback.Result != EResult.OK)
            {
                CommandHandler.ReplyToCommand(request.Command, "Unable to request player count: {0}{1}", Colors.RED, callback.Result);
            }
            else if (request.Target == 0)
            {
                CommandHandler.ReplyToCommand(
                    request.Command,
                    "{0}{1:N0}{2} people praising lord Gaben right now, influence:{3} {4}",
                    Colors.OLIVE, callback.NumPlayers, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(753, "graphs")
                );
            }
            else
            {
                CommandHandler.ReplyToCommand(
                    request.Command,
                    "People playing {0}{1}{2} right now: {3}{4:N0}{5} -{6} {7}",
                    Colors.BLUE, Steam.GetAppName(request.Target), Colors.NORMAL,
                    Colors.OLIVE, callback.NumPlayers, Colors.NORMAL,
                    Colors.DARKBLUE, SteamDB.GetAppURL(request.Target, "graphs")
                );
            }
        }
    }
}
