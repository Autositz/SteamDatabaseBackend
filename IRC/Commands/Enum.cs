﻿/*
 * Copyright (c) 2013-2015, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SteamKit2.Internal;

namespace SteamDatabaseBackend
{
    class EnumCommand : Command
    {
        IEnumerable<Type> SteamKitEnums;

        public EnumCommand()
        {
            Trigger = "enum";

            SteamKitEnums = typeof(CMClient).Assembly.GetTypes()
                .Where(x => x.IsEnum && x.Namespace.StartsWith("SteamKit2", StringComparison.Ordinal))
                // some inner namespaces have enums that have matching names, but we (most likely) want to match against the root enums
                // so we order by having the root enums first
                .OrderByDescending(x => x.Namespace == "SteamKit2");
        }

        public override void OnCommand(CommandArguments command)
        {
            if (command.Message.Equals("list", StringComparison.CurrentCultureIgnoreCase))
            {
                CommandHandler.ReplyToCommand(command, string.Join(", ", SteamKitEnums.Select(@enum => @enum.Name)));

                return;
            }

            var args = command.Message.Split(' ');

            if (args.Length < 1)
            {
                CommandHandler.ReplyToCommand(command, "Usage:{0} enum <enumname> [value or substring [deprecated]]", Colors.OLIVE);

                return;
            }

            var enumType = args[0].Replace("SteamKit2.", "");

            var matchingEnumType = SteamKitEnums
                .FirstOrDefault(x => x.Name.Equals(enumType, StringComparison.InvariantCultureIgnoreCase) || GetDottedTypeName(x).IndexOf(enumType, StringComparison.OrdinalIgnoreCase) != -1);

            if (matchingEnumType == null)
            {
                CommandHandler.ReplyToCommand(command, "No such enum type.");

                return;
            }

            bool includeDeprecated = args.Length > 2 && args[2].Equals("deprecated", StringComparison.InvariantCultureIgnoreCase);

            GetType().GetMethod("RunForEnum", BindingFlags.Instance | BindingFlags.NonPublic)
                .MakeGenericMethod(matchingEnumType)
                .Invoke(this, new object[] { args.Length > 1 ? args[1] : string.Empty, command, includeDeprecated });
        }

        void RunForEnum<TEnum>(string inputValue, CommandArguments command, bool includeDeprecated)
            where TEnum : struct
        {
            string enumName = GetDottedTypeName(typeof(TEnum));
            TEnum enumValue;

            if (Enum.TryParse(inputValue, out enumValue))
            {
                CommandHandler.ReplyToCommand(command, "{0}{1}{2} ({3}) ={4} {5}", Colors.LIGHTGRAY, enumName, Colors.NORMAL, Enum.Format(typeof(TEnum), enumValue, "D"), Colors.BLUE, enumValue);

                return;
            }
                
            var enumValues = Enum.GetValues(typeof(TEnum)).Cast<TEnum>();

            if (!includeDeprecated)
            {
                enumValues = enumValues.Except(enumValues.Where(x => typeof(TEnum).GetMember(x.ToString())[0].GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).Any()));
            }

            if (!string.IsNullOrEmpty(inputValue))
            {
                enumValues = enumValues.Where(x => x.ToString().IndexOf(inputValue, StringComparison.InvariantCultureIgnoreCase) >= 0);
            }

            var count = enumValues.Count();

            if (count == 0)
            {
                CommandHandler.ReplyToCommand(command, "No matches found.");

                return;
            }
            else if (count > 10)
            {
                if (!string.IsNullOrEmpty(inputValue))
                {
                    CommandHandler.ReplyToCommand(command, "More than 10 results found.");

                    return;
                }

                enumValues = enumValues.Take(10);
            }

            var formatted = string.Join(", ", enumValues.Select(@enum => string.Format("{0}{1}{2} ({3})", Colors.BLUE, @enum.ToString(), Colors.NORMAL, Enum.Format(typeof(TEnum), @enum, "D"))));

            if (count > 10)
            {
                formatted = string.Format("{0}, and {1} more...", formatted, count - 10);
            }

            CommandHandler.ReplyToCommand(command, "{0}{1}{2}: {3}", Colors.LIGHTGRAY, enumName, Colors.NORMAL, formatted);
        }

        private static string GetDottedTypeName(Type type)
        {
            // @VoiDeD:
            // naive implementation of programmer friendly type full names
            // ideally we'd want something like http://stackoverflow.com/a/28943180/139147
            // but bringing in codedom is probably like using a sledgehammer to open a sliding glass door

            string fullName = type.FullName;

            if (fullName == null)
            {
                return fullName;
            }

            fullName = fullName
                .Replace("+", ".")
                .Replace("SteamKit2.", "");

            return fullName;
        }
    }
}
