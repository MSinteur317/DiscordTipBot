﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using NLog;
using TipBot.Database.Models;
using TipBot.Helpers;
using TipBot.Logic;

namespace TipBot.CommandModules
{
    public class PublicCommands : TipbotModuleBase
    {
        /// <inheritdoc cref="CommandsManager"/>
        /// <remarks>
        /// Set by DI.
        /// <para>
        /// All access to it should be protected by <see cref="lockObject"/>.
        /// This basically reduces the amount of simultaneous calls the bot can handle and this is intentional
        /// because at the same time such approach guarantees that there will be no race conditions which might
        /// lead to funds being lost.
        /// </para>
        /// </remarks>
        public CommandsManager CommandsManager { get; set; }

        /// <inheritdoc cref="Settings"/>
        /// <remarks>Set by DI.</remarks>
        public Settings Settings { get; set; }

        /// <inheritdoc cref="MessagesHelper"/>
        /// <remarks>Set by DI.</remarks>
        public MessagesHelper MessagesHelper { get; set; }

        /// <summary>Protects access to <see cref="CommandsManager"/>.</summary>
        private readonly object lockObject = new object();

        private readonly string tadaEmoji = ":tada:";

        private readonly Logger logger = LogManager.GetCurrentClassLogger();

        [CommandWithHelp("tip", "Transfers specified amount of money to mentioned user.", "tipbot tip <user> <amount> <message>*")]
        public Task TipAsync(IUser userBeingTipped, decimal amount, [Remainder]string message = null)
        {
            this.logger.Trace("({0}:{1},{2}:{3},{4}:'{5}')", nameof(userBeingTipped), userBeingTipped.Id, nameof(amount), amount, nameof(message), message);

            IUser sender = this.Context.User;

            string response;

            lock (this.lockObject)
            {
                try
                {
                    this.CommandsManager.TipUser(sender, userBeingTipped, amount);

                    response = $"{sender.Mention} tipped {userBeingTipped.Mention} {amount} {this.Settings.Ticker}";

                    if (message != null)
                        response += $" with message `{message.Replace("`","")}`";
                }
                catch (CommandExecutionException exception)
                {
                    response = "Error: " + exception.Message;
                }
            }

            response = this.TrimMessage(response);

            this.logger.Trace("(-)");

            return this.EmbedReplyAsync("User tipped", response, includeAvatar: true);
        }

        [CommandWithHelp("deposit", "Displays your unique deposit address or assigns you one if it wasn't assigned before.")]
        public Task DepositAsync()
        {
            this.logger.Trace("()");

            SocketUser user = this.Context.User;

            string response;

            lock (this.lockObject)
            {
                try
                {
                    string depositAddress = this.CommandsManager.GetDepositAddress(user);

                    response = $"{user.Mention}, your unique deposit address is `{depositAddress}`";
                    response += Environment.NewLine + $"Money are deposited after {this.Settings.MinConfirmationsForDeposit} confirmations.";
                }
                catch (OutOfDepositAddressesException)
                {
                    response = "Bot ran out of deposit addresses. Tell bot admin about it.";
                }
            }

            response = this.TrimMessage(response);

            this.logger.Trace("(-)");

            return this.EmbedReplyAsync("Deposit", response);
        }

        [CommandWithHelp("withdraw", "Withdraws given amount to specified address. Fee will be subtracted from given amount." + "\n" +
                                     "Keep in mind that withdrawal address will be publicly visible to all users in this channel. " +
                                     "To avoid exposing your address use withdraw command in private messages with the bot.", "tipbot withdraw <amount> <address>")]
        public Task WithdrawAsync(decimal amount, string address)
        {
            this.logger.Trace("({0}:{1},{2}:{3})", nameof(amount), amount, nameof(address), address);

            IUser sender = this.Context.User;

            string response;

            lock (this.lockObject)
            {
                try
                {
                    this.CommandsManager.Withdraw(sender, amount, address);

                    response = $"{sender.Mention}, withdrawal of {amount} {this.Settings.Ticker} completed.";
                }
                catch (CommandExecutionException exception)
                {
                    response = "Error: " + exception.Message;
                }
            }

            response = this.TrimMessage(response);

            this.logger.Trace("(-)");
            return this.EmbedReplyAsync("Withdraw", response, includeAvatar: true);
        }

        [CommandWithHelp("balance", "Displays your current balance.")]
        public Task BalanceAsync()
        {
            this.logger.Trace("()");

            IUser sender = this.Context.User;

            string response;

            lock (this.lockObject)
            {
                decimal balance = this.CommandsManager.GetUserBalance(sender);

                response = $"{sender.Mention}, you have {balance} {this.Settings.Ticker}!";
            }

            response = this.TrimMessage(response);

            this.logger.Trace("(-)");
            return this.EmbedReplyAsync("Balance", response, includeAvatar: true);
        }

        [CommandWithHelp("makeItRain", "Randomly selects online users from the current server and tips them 1 coin (or another value if specified by caller)." +
                                       " Amount of users that will be tipped is equal to totalAmount / tipAmount.", "tipbot makeItRain <totalAmount> <tipAmount=1>*")]
        public async Task MakeItRainAsync(decimal amount, decimal tipAmount = 1)
        {
            this.logger.Trace("({0}:{1},{2}:{3})", nameof(amount), amount, nameof(tipAmount), tipAmount);

            IUser caller = this.Context.User;

            var onlineUsers = new List<IUser>();
            IAsyncEnumerable<IReadOnlyCollection<IUser>> usersCollection = this.Context.Channel.GetUsersAsync();

            await usersCollection.ForEachAsync(delegate (IReadOnlyCollection<IUser> users)
            {
                onlineUsers.AddRange(users.Where(x => x.Status != UserStatus.Offline && !x.IsBot));
            }).ConfigureAwait(false);

            onlineUsers.Remove(caller);

            string response;

            lock (this.lockObject)
            {
                try
                {
                    List<DiscordUserModel> usersBeingTipped = this.CommandsManager.RandomlyTipUsers(caller, onlineUsers, amount, tipAmount);

                    var builder = new StringBuilder();

                    builder.AppendLine($"{this.tadaEmoji}{caller.Mention} just tipped {usersBeingTipped.Count} users {tipAmount} {this.Settings.Ticker} each!{this.tadaEmoji}");
                    builder.AppendLine();

                    foreach (DiscordUserModel tippedUser in usersBeingTipped)
                    {
                        builder.Append($"<@!{tippedUser.DiscordUserId}>");

                        if (tippedUser != usersBeingTipped.Last())
                            builder.Append(", ");
                    }

                    builder.Append($" - you all received {tipAmount} {this.Settings.Ticker}!");

                    response = builder.ToString();
                }
                catch (CommandExecutionException exception)
                {
                    response = "Error: " + exception.Message;
                }
            }

            response = this.TrimMessage(response);

            this.logger.Trace("(-)");
            await this.EmbedReplyAsync("Make it rain!", response);
        }

        [CommandWithHelp("chart", "Displays top 3 tippers and users being tipped over the last 7 days.", "tipbot chart <days=7>*")]
        public async Task ChartAsync(int days = 7)
        {
            this.logger.Trace("({0}:{1})", nameof(days), days);

            string response;

            lock (this.lockObject)
            {
                try
                {
                    TippingChartsModel chart = this.CommandsManager.GetTopTippers(days, this.Settings.MaxChartUsersCount);

                    var builder = new StringBuilder();

                    // Best tippers.
                    if (chart.BestTippers.Count != 0)
                    {
                        builder.AppendLine($"Top {chart.BestTippers.Count} users who tipped the most in the last {days} days:");

                        foreach (UserViewModel tipper in chart.BestTippers)
                            builder.AppendLine($"**{tipper.UserName}** tipped {tipper.Amount} {this.Settings.Ticker}");
                    }
                    else
                        builder.AppendLine($"No one tipped anyone in the last {days} days!");

                    builder.AppendLine();

                    // Best being tipped.
                    if (chart.BestBeingTipped.Count != 0)
                    {
                        builder.AppendLine($"Top {chart.BestBeingTipped.Count} users who were tipped the most in the last {days} days:");

                        foreach (UserViewModel beingTipped in chart.BestBeingTipped)
                            builder.AppendLine($"**{beingTipped.UserName}** received {beingTipped.Amount} {this.Settings.Ticker}");
                    }
                    else
                        builder.AppendLine($"No one was tipped in the last {days} days!");

                    response = builder.ToString();
                }
                catch (CommandExecutionException exception)
                {
                    response = "Error: " + exception.Message;
                }
            }

            response = this.TrimMessage(response);

            this.logger.Trace("(-)");
            await this.EmbedReplyAsync("Chart", response);
        }

        [CommandWithHelp("startQuiz", "You ask a question, supply hash of an answer and for how long the quiz will be running." +
                                       " First user to provide correct answer gets the prize! In case no one answers money will return back to you after quiz expiry." +
                                       " For hash generation use <https://passwordsgenerator.net/sha256-hash-generator/>",
                                        "tipbot startQuiz <amount> <SHA256 of an answer> <duration in minutes> <question>")]
        public async Task StartQuizAsync(decimal amount, string answerSHA256, int durationMinutes, [Remainder]string question)
        {
            this.logger.Trace("({0}:{1},{2}:'{3}',{4}:{5},{6}:'{7}')", nameof(amount), amount, nameof(answerSHA256), answerSHA256, nameof(durationMinutes), durationMinutes, nameof(question), question);

            IUser user = this.Context.User;

            string response;

            lock (this.lockObject)
            {
                try
                {
                    this.CommandsManager.StartQuiz(user, amount, answerSHA256, durationMinutes, question);

                    response = $"{user.Mention} started a quiz!" + Environment.NewLine +
                               $"Question is: `{question}`" + Environment.NewLine +
                               $"You have {durationMinutes} minutes to answer correctly and claim {amount} {this.Settings.Ticker}!" + Environment.NewLine +
                               $"If no one answers before time runs out {amount} {this.Settings.Ticker} will be returned to {user.Mention}.";

                    // Remove original message to hide hash.
                    this.MessagesHelper.SelfDestruct(this.Context.Message, 0);
                }
                catch (CommandExecutionException exception)
                {
                    response = "Error: " + exception.Message;
                }
            }

            response = this.TrimMessage(response);

            this.logger.Trace("(-)");
            await this.EmbedReplyAsync("Start quiz", response);
        }

        [CommandWithHelp("answerQuiz", "Answer to any active quiz. Answer will be checked against all of them. In case your answer will be correct you'll receive a reward.",
            "tipbot answerQuiz <answer>")]
        public async Task AnswerQuizAsync([Remainder]string answer)
        {
            this.logger.Trace("({0}:'{1}')", nameof(answer), answer);

            IUser user = this.Context.User;

            string response;
            bool deleteMessage = false;

            lock (this.lockObject)
            {
                try
                {
                    AnswerToQuizResponseModel result = this.CommandsManager.AnswerToQuiz(user, answer);

                    if (!result.Success)
                    {
                        response = "Unfortunately you are not correct, that's not the answer to any of the active quizes." +
                                    Environment.NewLine + Environment.NewLine +
                                   $"_`This and your message will be removed in {this.Settings.SelfDestructedMessagesDelaySeconds} seconds to avoid bloating the channel.`_";

                        this.MessagesHelper.SelfDestruct(this.Context.Message, this.Settings.SelfDestructedMessagesDelaySeconds);

                        deleteMessage = true;
                    }
                    else
                    {
                        response = $"{user.Mention} bingo!" + Environment.NewLine +
                                   $"Question was `{result.QuizQuestion}`" + Environment.NewLine +
                                   $"And the answer is `{answer}`" + Environment.NewLine +
                                   $"Your reward of {result.Reward} {this.Settings.Ticker} was deposited to your account!" + Environment.NewLine +
                                   $"<@!{result.QuizCreatorDiscordUserId}>, your quiz was solved!";
                    }
                }
                catch (CommandExecutionException exception)
                {
                    response = "Error: " + exception.Message;
                }
            }

            response = this.TrimMessage(response);

            if (deleteMessage)
            {
                await this.MessagesHelper.SendSelfDesctructedMessage(this.Context, response, false).ConfigureAwait(false);
            }
            else
            {
                await this.EmbedReplyAsync("Answer quiz", response);
            }

            this.logger.Trace("(-)");
        }

        [CommandWithHelp("quizzes", "Displays all quizzes that are active.")]
        public Task ListActiveQuizes()
        {
            this.logger.Trace("()");

            lock (this.lockObject)
            {
                var builder = new StringBuilder();
                List<QuizViewModel> quizes = this.CommandsManager.GetActiveQuizes();

                if (quizes.Count != 0)
                {
                    builder.AppendLine("__List of all active quizes:__");
                    builder.AppendLine();

                    foreach (QuizViewModel quiz in quizes)
                    {
                        builder.AppendLine($"Question: `{quiz.Question}`");
                        builder.AppendLine($"Reward: **{quiz.Reward}** {this.Settings.Ticker}");
                        builder.AppendLine($"Created by: **{quiz.DiscordUserName}**");

                        var minutesLeft = (int) ((quiz.CreationTime + TimeSpan.FromMinutes(quiz.DurationMinutes)) - DateTime.Now).TotalMinutes;
                        if (minutesLeft < 0)
                            minutesLeft = 0;

                        builder.AppendLine($"Expires in {minutesLeft} minutes.");
                        builder.AppendLine();
                    }
                }
                else
                {
                    builder.AppendLine("There are no active quizes.");
                    builder.AppendLine("Start a new one yourself using `startQuiz` command!");
                }

                string response = this.TrimMessage(builder.ToString());

                this.logger.Trace("(-)");
                return this.EmbedReplyAsync("List active quizzes", response);
            }
        }

        [Command("help")]
        public Task HelpAsync()
        {
            this.logger.Trace("()");

            var helpAttributes = new List<CommandWithHelpAttribute>();

            foreach (MemberInfo memberInfo in this.GetType().GetMembers())
            {
                foreach (CommandWithHelpAttribute attribute in memberInfo.GetCustomAttributes(typeof(CommandWithHelpAttribute), true).ToList())
                    helpAttributes.Add(attribute);
            }

            var builder = new StringBuilder();

            builder.AppendLine("__List of bot commands:__");
            builder.AppendLine("parameters marked with * are optional");
            builder.AppendLine("");

            foreach (CommandWithHelpAttribute helpAttr in helpAttributes)
            {
                string helpStr = $"**{helpAttr.Text}**- {helpAttr.HelpInfo}";

                if (helpAttr.UsageExample != null)
                {
                    helpStr += Environment.NewLine + "`" + helpAttr.UsageExample + "`";
                }

                builder.AppendLine(helpStr);
                builder.AppendLine();
            }

            string response = this.TrimMessage(builder.ToString());

            this.logger.Trace("(-)");
            return this.EmbedReplyAsync("Help", response);
        }

        [CommandWithHelp("about", "Displays information about the bot.")]
        public Task AboutAsync()
        {
            this.logger.Trace("()");

            Stream stream = this.GetLogo();

            Version version = Assembly.GetExecutingAssembly().GetName().Version;

            string text = "`TipBot`" + Environment.NewLine + $"Version: {version}" + Environment.NewLine + "Github: <https://github.com/noescape00/DiscordTipBot>";

            this.logger.Trace("(-)");
            return this.Context.Channel.SendFileAsync(stream, "logo.png", text);
        }

        /// <summary>Trims the message to be shorter than 2000 characters.</summary>
        private string TrimMessage(string message)
        {
            this.logger.Trace("({0}:{1})", nameof(message), message.Length);

            var limit = 2000;

            if (message.Length < limit)
                return message;

            string trimmed = message.Substring(0, limit - 3) + "...";

            this.logger.Trace("()");
            return trimmed;
        }

        private Stream GetLogo()
        {
            this.logger.Trace("()");
            Assembly assembly = this.GetType().GetTypeInfo().Assembly;
            Stream stream = assembly.GetManifestResourceStream("TipBot.Content.Logo.png");

            stream.Seek(0, SeekOrigin.Begin);

            this.logger.Trace("(-)");
            return stream;
        }
    }
}
