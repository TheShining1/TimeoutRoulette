using System;
using System.Reflection;
using System.Collections;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Timers;

public class CPHInline
{
    static string LogPrefix = "TSO::Roulette::";
    static RouletteConfig config = new RouletteConfig();
    // Variables to store the reward ID and redemption ID for Twitch Channel Points.
    // Added to ensure that Channel Points can be correctly handled and potentially refunded in case of errors or cancellations.
    static string rewardId = "";
    static string redemptionId = "";
    static int MaxNumberOfPlayers;
    static int Round;
    [Flags]
    enum GameStates : byte
    {
        None = 0,
        Started = 1 << 1,
        Singleplayer = 1 << 2,
        Multiplayer = 1 << 3,
        Duel = 1 << 4,
        Normal = 1 << 5,
        Knockout = 1 << 6,
        InProgress = 1 << 7
    }

    static GameStates GameState;
    static OrderedDictionary Players;
    static int PlayerIndex;
    static Timer TimeoutTimer = new Timer();
    // A timer used to reassign moderator status after a timeout.
    static Timer RemodeTimer;

    public bool SetConfig()
    {
        bool noErrors = true;
        Type t = config.GetType();
        PropertyInfo[] props = t.GetProperties();
        foreach (var prop in props)
        {
            Type propType = prop.PropertyType;
            object propValue;
            if (!CPH.TryGetArg(prop.Name, out propValue))
            {
                var existingValue = prop.GetValue(config);
                if (existingValue == null || string.IsNullOrEmpty(existingValue.ToString()))
                {
                    noErrors = false;
                    CPH.LogWarn(String.Format("{0}Config argument {1} is not set, check roulette_Config actions", LogPrefix, prop.Name));
                    continue;
                }
            }
            else
            {
                prop.SetValue(config, Convert.ChangeType(propValue, propType));
                CPH.LogInfo(String.Format("{0}Config argument {1} is set to value {2}", LogPrefix, prop.Name, propValue));
            }
        }
        return noErrors;
    }

    public bool Reset()
    {
        CPH.LogInfo(String.Format("{0}Game was reset", LogPrefix));
        rewardId = "";
        redemptionId = "";
        MaxNumberOfPlayers = config.getMaxNumberOfPlayers();
        Round = 0;
        GameState = GameStates.None;
        Players = new OrderedDictionary();
        PlayerIndex = 0;
        TimerStop();
        return true;
    }

    bool IsChannelPointReward()
    {
        // Adds logging to indicate when the check for a Channel Point Reward event is performed.
        CPH.LogInfo(String.Format("{0}Checking if event is a Channel Point Reward", LogPrefix));
        // Extends the check to consider if the rewardId has already been set, improving robustness for various event scenarios.
        return CPH.GetEventType() == Streamer.bot.Common.Events.EventType.TwitchRewardRedemption || !String.IsNullOrEmpty(rewardId);
    }

    void RefundPoints()
    {
        CPH.LogInfo(String.Format("{0}Refunding points for rewardId: {1}, redemptionId: {2}", LogPrefix, rewardId, redemptionId));
        try
        {
            // Attempts to cancel the Twitch redemption using the provided rewardId and redemptionId.
            CPH.TwitchRedemptionCancel(rewardId, redemptionId);
            CPH.LogInfo(String.Format("{0}Refund successful", LogPrefix));
        }
        catch (Exception ex)
        {
            CPH.LogError(String.Format("{0}Refund failed. Exception: {1}", LogPrefix, ex.ToString()));
        }
    }

    void GameIsRunning(string commandSource, string user)
    {
        CPH.LogInfo(String.Format("{0}Game is already running", LogPrefix));
        var message = String.Format(config.gameIsRunningMessage, user);
        SendMessage(commandSource, message);
        // Retrieves the reward and redemption IDs for the current attempt (usually the second user trying to start the game).
        string currentRewardId = "";
        string currentRedemptionId = "";
        if (CPH.TryGetArg<string>("rewardId", out currentRewardId) && CPH.TryGetArg<string>("redemptionId", out currentRedemptionId))
        {
            CPH.LogDebug(String.Format("{0}Attempting to refund points for user {1}. rewardId: {2}, redemptionId: {3}", LogPrefix, user, currentRewardId, currentRedemptionId));
            try
            {
                CPH.TwitchRedemptionCancel(currentRewardId, currentRedemptionId);
                CPH.LogDebug(String.Format("{0}Refund successful for user {1}. rewardId: {2}, redemptionId: {3}", LogPrefix, user, currentRewardId, currentRedemptionId));
            }
            catch (Exception ex)
            {
                CPH.LogError(String.Format("{0}Refund failed for user {1}. rewardId: {2}, redemptionId: {3}. Exception: {4}", LogPrefix, user, currentRewardId, currentRedemptionId, ex.ToString()));
            }
        }
        else
        {
            CPH.LogWarn(String.Format("{0}No valid rewardId or redemptionId found for user {1}. Refund not possible.", LogPrefix, user));
        }
    }

    public bool IsRunning()
    {
        var user = args["user"].ToString();
        string commandSource = args["userType"].ToString();
        if (GameState != GameStates.None)
        {
            GameIsRunning(commandSource, user);
            return false;
        }
        return true;
    }

    public bool Execute()
    {
        CPH.LogWarn(String.Format("{0}To use this game, use roulette_Start action.", LogPrefix));
        return true;
    }

    public bool Run()
    {
        CPH.LogInfo(String.Format("{0}Starting game", LogPrefix));
        Reset();

        string user;
        string commandSource;

        if (!CPH.TryGetArg<string>("user", out user) || string.IsNullOrEmpty(user))
        {
            CPH.LogError($"{LogPrefix}Error: 'user' argument is null or empty.");
            return false;
        }

        if (!CPH.TryGetArg<string>("userType", out commandSource) || string.IsNullOrEmpty(commandSource))
        {
            CPH.LogError($"{LogPrefix}Error: 'userType' argument is null or empty.");
            return false;
        }

        if (IsChannelPointReward())
        {
            CPH.TryGetArg<string>("rewardId", out rewardId);
            CPH.TryGetArg<string>("redemptionId", out redemptionId);
            CPH.LogInfo(String.Format("{0}Channel Point Reward detected, rewardId: {1}, redemptionId: {2}", LogPrefix, rewardId, redemptionId));
        }

        Start(commandSource, user);
        return true;
    }

    void Start(string commandSource, string user)
    {
        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(commandSource))
        {
            CPH.LogError($"{LogPrefix}Error: 'user' or 'commandSource' is null or empty in Start method.");
            return;
        }

        GameState = GameStates.Started;
        Players.Add(user, commandSource);
        var message = String.Format(config.startMessage, user, config.startTimeout);
        SendMessage(commandSource, message);
        message = String.Format(config.startTimeoutMessage, user);
        TimeoutTimer = TimerStart(commandSource, message, config.startTimeout, OnStartTimeout);
    }

    public bool Singleplayer()
    {
        string user;
        CPH.TryGetArg<string>("user", out user);
        if (!isOwner(user) || (GameState & GameStates.Started) == 0)
            return false;

        CPH.LogInfo(String.Format("{0}Singleplayer game started by {1}", LogPrefix, user));
        GameState = GameStates.Singleplayer;
        TimerStop();
        CPH.PlaySound(config.barrelSoundPath, 1.0f, true);
        string commandSource;
        CPH.TryGetArg<string>("userType", out commandSource);
        var message = String.Format(config.handedGunMessage, user, config.numberOfChambers);
        SendMessage(commandSource, message);

        if (GameRound(commandSource, user))
            Shot(commandSource, user);
        else
            Miss(commandSource, user);

        Reset();
        return true;
    }

    public bool ChooseType()
    {
        string user = "";
        CPH.TryGetArg<string>("user", out user);
        if (!isOwner(user) || (GameState & GameStates.Started) == 0)
            return false;

        CPH.LogInfo(String.Format("{0}Choosing game type", LogPrefix));
        TimerStop();
        GameState = GameStates.Multiplayer;
        string command = "";
        CPH.TryGetArg<string>("command", out command);
        if (command == "!duel")
            GameState |= GameStates.Duel;

        string commandSource = "";
        CPH.TryGetArg<string>("userType", out commandSource);
        var message = String.Format(config.gameTypeMessage, user, config.typeTimeout);
        SendMessage(commandSource, message);
        message = String.Format(config.typeTimeoutMessage, user);
        TimeoutTimer = TimerStart(commandSource, message, config.typeTimeout, OnTypeTimeout);
        return true;
    }

    public bool StartNormal()
    {
        string user = "";
        if (!CPH.TryGetArg<string>("user", out user) || !isOwner(user) || (GameState & GameStates.InProgress) != 0 || (GameState & (GameStates.Normal | GameStates.Knockout)) != 0 || (GameState & GameStates.Multiplayer) == 0)
            return false;

        CPH.LogInfo(String.Format("{0}Normal game type was chosen", LogPrefix));
        GameState |= GameStates.Normal;
        TimerStop();
        string commandSource = args["userType"].ToString();
        var message = String.Format(config.normalGameAnnouncement, user);
        SendMessage(commandSource, message);

        if ((GameState & GameStates.Duel) == 0)
            StartMultiplayer(commandSource, user);
        else
            StartDuel(commandSource, user);

        return true;
    }

    public bool StartKnockout()
    {
        string user = "";
        if (!CPH.TryGetArg<string>("user", out user) || !isOwner(user) || (GameState & GameStates.InProgress) != 0 || (GameState & (GameStates.Normal | GameStates.Knockout)) != 0 || (GameState & GameStates.Multiplayer) == 0)
            return false;

        CPH.LogInfo(String.Format("{0}Knockout game type was chosen", LogPrefix));
        GameState |= GameStates.Knockout;
        TimerStop();
        string commandSource = args["userType"].ToString();
        var message = String.Format(config.knockoutGameAnnouncement, user);
        SendMessage(commandSource, message);

        if ((GameState & GameStates.Duel) == 0)
            StartMultiplayer(commandSource, user);
        else
            StartDuel(commandSource, user);

        return true;
    }

    void StartMultiplayer(string commandSource, string user)
    {
        CPH.LogInfo(String.Format("{0}Multiplayer game announced", LogPrefix));
        var message = String.Format(config.multiplayerAnnouncement, user, MaxNumberOfPlayers, config.joinTimeout);
        SendMessage(commandSource, message);
        message = String.Format(config.joinTimeoutMessage, user);
        TimeoutTimer = TimerStart(commandSource, message, config.joinTimeout, OnJoinTimeout);
    }

    void StartDuel(string commandSource, string user)
    {
        CPH.LogInfo(String.Format("{0}Duel announced", LogPrefix));
        MaxNumberOfPlayers = 2;
        var message = String.Format(config.duelAnnouncement, user, config.joinTimeout);
        SendMessage(commandSource, message);
        message = String.Format(config.joinTimeoutMessage, user);
        TimeoutTimer = TimerStart(commandSource, message, config.joinTimeout, OnJoinTimeout);
    }

    public bool Join()
    {
        if ((GameState & GameStates.InProgress) != 0 || (GameState & (GameStates.Normal | GameStates.Knockout)) == 0)
            return false;

        var user = args["user"].ToString();
        string commandSource = args["userType"].ToString();
        if (Players.Contains(user))
        {
            AlreadyJoined(commandSource, user);
            return false;
        }

        if (Players.Count >= MaxNumberOfPlayers)
        {
            var message = String.Format(config.gameIsFullMessage, user);
            SendMessage(commandSource, message);
            TimerStop();
            Multiplayer();
            return false;
        }

        Joining(commandSource, user);
        if (Players.Count == MaxNumberOfPlayers)
            Multiplayer();

        return true;
    }

    void Multiplayer()
    {
        if ((GameState & GameStates.InProgress) != 0)
            return;

        GameState |= GameStates.InProgress;
        TimerStop();
        CPH.LogInfo(String.Format("{0}Multiplayer game starting with {1} players", LogPrefix, Players.Count));
        string message;
        ICollection pDictKeys = Players.Keys;
        String[] pKeys = new String[Players.Count];
        pDictKeys.CopyTo(pKeys, 0);
        var commandSource = Players[0].ToString();
        if ((GameState & GameStates.Duel) != 0)
            message = String.Format(config.duelStartMessage, pKeys[0], pKeys[1]);
        else
            message = String.Format(config.multiplayerStartMessage, Players.Count, String.Join(", ", pKeys));
        SendMessage(commandSource, message);
        CPH.PlaySound(config.barrelSoundPath, 1.0f, true);
        message = String.Format(config.handedGunMessage, pKeys[0], config.numberOfChambers);
        SendMessage(commandSource, message);

        do
        {
            foreach (DictionaryEntry player in Players)
            {
                string user = player.Key.ToString();
                string source = player.Value.ToString();
                if (GameRound(source, user))
                {
                    Shot(source, user);
                    Reset();
                    return;
                }
                Miss(source, user);
                config.numberOfChambers--;
            }
        }
        while ((GameState & GameStates.Knockout) != 0);

        message = String.Format(config.multiplayerEndMessage);
        SendMessage(commandSource, message);
        Reset();
    }

    bool GameRound(string commandSource, string user)
    {
        Round++;
        if ((GameState & GameStates.Multiplayer) != 0)
        {
            var message = String.Format(config.turnMessage, user);
            SendMessage(commandSource, message);
        }

        if (RollHesitation())
            Hesitate(commandSource, user);
        else
            NoHesitate(commandSource, user);

        return RollShot();
    }

    void AlreadyJoined(string source, string user)
    {
        var message = String.Format(config.alreadyJoinedMessage, user);
        SendMessage(source, message);
    }

    void Joining(string source, string user)
    {
        PlayerIndex++;
        Players.Add(user, source);
        var message = String.Format(config.joinedMessage, user);
        SendMessage(source, message);
    }

    bool RollHesitation()
    {
        var rnd = CPH.Between(1000, 5000);
        CPH.Wait(rnd);
        return rnd > 2000;
    }

    void NoHesitate(string commandSource, string user)
    {
        var message = String.Format(config.noHesitationMessage, user);
        SendMessage(commandSource, message);
        CPH.Wait(500);
    }

    void Hesitate(string commandSource, string user)
    {
        var message = String.Format(config.hesitationMessage, user);
        SendMessage(commandSource, message);
        CPH.Wait(500);
    }

    bool RollShot()
    {
        return CPH.Between(0, config.numberOfChambers - Round) == 0;
    }

    // EmptyProfile : Adapted the "Shot" method to make it work with a game "leaderboard" : https://github.com/Mouchoir/TimeoutRouletteLeaderboard
    public void Shot(string commandSource, string user)
    {
        int losses;
        int timeoutMinutes;

        if (commandSource == "twitch")
        {
            // Retrieve the user's current loss count using the Twitch method
            losses = CPH.GetTwitchUserVar<int>(user, "losses", true);
            // Retrieve the user's current total timeout minutes using the Twitch method
            timeoutMinutes = CPH.GetTwitchUserVar<int>(user, "timeoutMinutes", true);

            // Increment and update values for Twitch
            losses += 1;
            timeoutMinutes += config.twitchTimeoutDuration;
            CPH.SetTwitchUserVar(user, "losses", losses, true);
            CPH.SetTwitchUserVar(user, "timeoutMinutes", timeoutMinutes, true);
        }
        else if (commandSource == "youtube")
        {
            // Retrieve the user's current loss count using the YouTube method
            losses = CPH.GetYouTubeUserVar<int>(user, "losses", true);
            // Retrieve the user's current total timeout minutes using the YouTube method
            timeoutMinutes = CPH.GetYouTubeUserVar<int>(user, "timeoutMinutes", true);

            // Increment and update values for YouTube
            losses += 1;
            timeoutMinutes += config.twitchTimeoutDuration; // Assume the same timeout duration is used for YouTube
            CPH.SetYouTubeUserVar(user, "losses", losses, true);
            CPH.SetYouTubeUserVar(user, "timeoutMinutes", timeoutMinutes, true);
        }
        else
        {
            CPH.LogError($"{LogPrefix}Unsupported platform: {commandSource}");
            return;
        }

        // Load the leaderboard from the persisted global variable
        var leaderboard = CPH.GetGlobalVar<Dictionary<string, (int losses, int timeoutMinutes)>>("rouletteLeaderboard", true) ?? new Dictionary<string, (int losses, int timeoutMinutes)>();

        // Update or add the user in the leaderboard with cumulative values
        if (leaderboard.ContainsKey(user))
        {
            var currentData = leaderboard[user];
            leaderboard[user] = (currentData.losses + 1, currentData.timeoutMinutes + config.twitchTimeoutDuration);
        }
        else
        {
            leaderboard[user] = (losses, timeoutMinutes);
        }

        // Save the updated leaderboard back to the persisted global variable
        CPH.SetGlobalVar("rouletteLeaderboard", leaderboard, true);
        CPH.LogInfo($"{LogPrefix}Player {user} - Cumulative Losses: {losses}, Cumulative Timeout: {timeoutMinutes} minutes");

        // Play the shot sound
        CPH.PlaySound(config.shotSoundPath, 1.0f, true);

        // Send a message to chat
        var message = String.Format(config.shotMessage, user);
        SendMessage(commandSource, message);

        // Apply the timeout to the user
        Timeout(commandSource, user);
    }

    void Miss(string commandSource, string user)
    {
        CPH.PlaySound(config.missSoundPath, 1.0f, true);
        var message = String.Format(config.missMessage, user);
        SendMessage(commandSource, message);
    }

    void Timeout(string commandSource, string user)
    {
        string message;
        int duration = config.twitchTimeoutDuration * 60;
        if (commandSource == "twitch")
        {
            if (IsModerator(user))
            {
                RemodeTimer = RemodeTimeout(user, duration + 10);
            }
            message = String.Format(config.timeoutMessageTW, user);
            // Removed config.twitchUseBotAccount param to allow the game to be handled by the bot account while the kick aciton is made by the streamer account, allowing to kick moderators accounts
            CPH.TwitchTimeoutUser(user, duration, config.twitchTimeoutReason);
        }
        else if (commandSource == "youtube")
        {
            message = String.Format(config.timeoutMessageYT, user);
            // Apply YouTube-specific timeout logic here if available
        }
        else
        {
            CPH.LogError($"{LogPrefix}Unsupported platform: {commandSource}");
            return;
        }
        SendMessage(commandSource, message);
    }

    // Automatically gives back the mod role to any moderator who was kicked playing the game
    Timer RemodeTimeout(string user, int duration)
    {
        var timer = new System.Timers.Timer(duration * 1000);
        timer.Elapsed += OnRemodTimeout(user);
        timer.AutoReset = false;
        timer.Enabled = true;
        return timer;
    }

    bool isOwner(string user)
    {
        ICollection pDictKeys = Players.Keys;
        String[] pKeys = new String[Players.Count];
        pDictKeys.CopyTo(pKeys, 0);
        return pKeys[0] == user;
    }

    bool IsModerator(string user)
    {
        TwitchUserInfo userInfo = CPH.TwitchGetUserInfoByLogin(user);
        return userInfo.IsModerator;
    }

    void SendMessage(string source, string message)
    {
        if (source == "twitch")
            CPH.SendMessage(message, config.twitchUseBotAccount);
        else if (source == "youtube")
            CPH.SendYouTubeMessage(message);
        else
            CPH.LogError($"{LogPrefix}Unsupported platform: {source}");
    }

    Timer TimerStart(string commandSource, string message, int duration, Func<string, string, ElapsedEventHandler> timeoutFunc)
    {
        var timer = new System.Timers.Timer(duration * 1000);
        timer.Elapsed += timeoutFunc(commandSource, message);
        timer.AutoReset = false;
        timer.Enabled = true;
        return timer;
    }

    void TimerStop()
    {
        TimeoutTimer.Stop();
        TimeoutTimer.Dispose();
    }

    ElapsedEventHandler OnRemodTimeout(string user)
    {
        return (Object source, System.Timers.ElapsedEventArgs e) =>
        {
            CPH.TwitchAddModerator(user);
            RemodeTimer.Stop();
            RemodeTimer.Dispose();
        };
    }

    ElapsedEventHandler OnStartTimeout(string commandSource, string message)
    {
        return (Object source, System.Timers.ElapsedEventArgs e) =>
        {
            SendMessage(commandSource, message);
            if (IsChannelPointReward())
                RefundPoints();
            Reset();
        };
    }

    ElapsedEventHandler OnTypeTimeout(string commandSource, string message)
    {
        return (Object source, System.Timers.ElapsedEventArgs e) =>
        {
            SendMessage(commandSource, message);
            StartNormal();
        };
    }

    ElapsedEventHandler OnJoinTimeout(string commandSource, string message)
    {
        return (Object source, System.Timers.ElapsedEventArgs e) =>
        {
            if (Players.Count >= 2) // Replace 2 with the minimum required number of players if necessary
            {
                Multiplayer();
                return;
            }
            if (IsChannelPointReward())
                RefundPoints();
            SendMessage(commandSource, message);
            Reset();
        };
    }
}

class RouletteConfig
{
    public string barrelSoundPath { get; set; }
    public string shotSoundPath { get; set; }
    public string missSoundPath { get; set; }
    public int numberOfChambers { get; set; }
    public int startTimeout { get; set; }
    public int typeTimeout { get; set; }
    public int joinTimeout { get; set; }
    public int twitchTimeoutDuration { get; set; }
    public string twitchTimeoutReason { get; set; }
    public bool twitchUseBotAccount { get; set; }

    public int getMaxNumberOfPlayers()
    {
        return numberOfChambers - 1;
    }

    public string startMessage { get; set; }
    public string gameTypeMessage { get; set; }
    public string gameIsRunningMessage { get; set; }
    public string gameIsFullMessage { get; set; }
    public string startTimeoutMessage { get; set; }
    public string typeTimeoutMessage { get; set; }
    public string joinTimeoutMessage { get; set; }
    public string normalGameAnnouncement { get; set; }
    public string knockoutGameAnnouncement { get; set; }
    public string duelAnnouncement { get; set; }
    public string duelStartMessage { get; set; }
    public string multiplayerAnnouncement { get; set; }
    public string alreadyJoinedMessage { get; set; }
    public string joinedMessage { get; set; }
    public string multiplayerStartMessage { get; set; }
    public string multiplayerEndMessage { get; set; }
    public string handedGunMessage { get; set; }
    public string turnMessage { get; set; }
    public string hesitationMessage { get; set; }
    public string noHesitationMessage { get; set; }
    public string shotMessage { get; set; }
    public string missMessage { get; set; }
    public string timeoutMessageYT { get; set; }
    public string timeoutMessageTW { get; set; }
}
