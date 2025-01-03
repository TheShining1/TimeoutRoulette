using System;
using System.Reflection;
using System.Collections;
using System.Collections.Specialized;
using System.Timers;

public class CPHInline
{
	static string LogPrefix = "TSO::Roulette::";	

	static RouletteConfig config = new RouletteConfig();

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
				noErrors = false;
				CPH.LogWarn(String.Format("{0}Config argument {1} is not set, check roulette_Config actions", LogPrefix, prop.Name));
				continue;
			}

			prop.SetValue(config, Convert.ChangeType(propValue, propType));
			CPH.LogVerbose(String.Format("{0}Config argument {1} is set to value {2} of type {3}", LogPrefix, prop.Name, propValue, propType));
		}
		
		if (config.twitchUseBotAccount)
		{
			var isBotAbsent = CPH.TwitchGetBot() is null;
			if (isBotAbsent) 
			{
				config.twitchUseBotAccount = false;
				CPH.LogWarn(String.Format("{0}No bot account connected. Default all commands to broadcaster account.", LogPrefix));			
			}
		}

		return noErrors;
	}

	public bool Reset()
	{
		CPH.LogDebug(String.Format("{0}Game was reseted", LogPrefix));
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
		CPH.LogWarn(String.Format("{0}CPH.GetEventType(): {1}", LogPrefix, CPH.GetEventType()));
		CPH.LogWarn(String.Format("{0}rewardId: {1}", LogPrefix, rewardId));
		CPH.LogWarn(String.Format("{0}String.IsNullOrEmpty(rewardId): {1}", LogPrefix, String.IsNullOrEmpty(rewardId)));
		return CPH.GetEventType() == Streamer.bot.Common.Events.EventType.TwitchRewardRedemption || !String.IsNullOrEmpty(rewardId);
	}

	void RefundPoints(string rewardId, string redemptionId)
	{
		CPH.TwitchRedemptionCancel(rewardId, redemptionId);
	}

	void GameIsRunning(string commandSource, string user)
	{
		CPH.LogDebug(String.Format("{0}Game is already running", LogPrefix));
		var message = String.Format(config.gameIsRunningMessage, user);
		SendMessage(commandSource, message);
		if (IsChannelPointReward()) 
		{
			string rewardId, redemptionId;
			if (CPH.TryGetArg<string>("rewardId", out rewardId)
				&& CPH.TryGetArg<string>("redemptionId", out redemptionId))
				{
					CPH.LogWarn(String.Format("{0}rewardId: {1}", LogPrefix, rewardId));
					CPH.LogWarn(String.Format("{0}redemptionId: {1}", LogPrefix, redemptionId));

					RefundPoints(rewardId, redemptionId);
				}
		}
	}

	public bool IsRuning()
	{
		CPH.LogDebug(String.Format("{0}IsRuning", LogPrefix));
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
		CPH.LogWarn(String.Format("{0}To use this game use roulette_Start action.", LogPrefix));
		return true;
	}

	public bool Run()
	{
		CPH.LogDebug(String.Format("{0}Execute", LogPrefix));
		Reset();	

		string commandSource = "";
		CPH.TryGetArg<string>("userType", out commandSource);

		string user;
		switch(commandSource)
		{
			case "twitch":				
				CPH.TryGetArg<string>("user", out user);
				break;
			case "youtube":
				CPH.TryGetArg<string>("userName", out user);
				break;
			default:
				CPH.TryGetArg<string>("user", out user);
				break;
		}		
		
		if(IsChannelPointReward())
		{			
			CPH.TryGetArg<string>("rewardId", out rewardId);
			CPH.TryGetArg<string>("redemptionId", out redemptionId);
			CPH.LogWarn(String.Format("{0}rewardId: {1}", LogPrefix, rewardId));
			CPH.LogWarn(String.Format("{0}redemptionId: {1}", LogPrefix, redemptionId));
		}

		Start(commandSource, user);

		return true;
	}

	void Start(string commandSource, string user)
	{
		CPH.LogDebug(String.Format("{0}Starting", LogPrefix));
		GameState = GameStates.Started;

		Players.Add(user, commandSource);

		if(config.singlePlayerMode) {
			CPH.LogDebug(String.Format("{0}Running singplayer only mode", LogPrefix));
			Singleplayer();
			return;
		}

		var message = String.Format(config.startMessage, user, config.startTimeout);
		SendMessage(commandSource, message);

		message = String.Format(config.startTimeoutMessage, user);
		TimeoutTimer = TimerStart(commandSource, message, config.startTimeout, OnStartTimeout);
	}

	public bool Singleplayer()
	{
		string commandSource;
		CPH.TryGetArg<string>("userType", out commandSource);

		string user;
		switch(commandSource)
		{
			case "twitch":				
				CPH.TryGetArg<string>("user", out user);
				break;
			case "youtube":
				CPH.TryGetArg<string>("userName", out user);
				break;
			default:
				CPH.TryGetArg<string>("user", out user);
				break;
		}

		if (!isOwner(user))
			return false;
		if ((GameState & GameStates.Started) == 0)
			return false;

		CPH.LogDebug(String.Format("{0}Singleplayer game started", LogPrefix));
		GameState = GameStates.Singleplayer;

		TimerStop();

		CPH.PlaySound(config.barrelSoundPath, 1.0f, true);
		
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
		
		if (!isOwner(user))
			return false;
		if ((GameState & GameStates.Started) == 0)
			return false;

		CPH.LogDebug(String.Format("{0}Choosing game type", LogPrefix));
		TimerStop();
		GameState = GameStates.Multiplayer;


		string command = "";
		CPH.TryGetArg<string>("command", out command);
		
		if (command == "!duel") GameState = GameState | GameStates.Duel;

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
		if(!CPH.TryGetArg<string>("user", out user)) return false;
		
		if (!isOwner(user)) return false;

		if ((GameState & GameStates.InProgress) != 0) return false;
		if ((GameState & (GameStates.Normal | GameStates.Knockout)) != 0) return false;
		if ((GameState & GameStates.Multiplayer) == 0) return false;

		CPH.LogDebug(String.Format("{0}Normal game type was chosen", LogPrefix));
		GameState = GameState | GameStates.Normal;
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
		if(!CPH.TryGetArg<string>("user", out user)) return false;
		
		if (!isOwner(user)) return false;

		if ((GameState & GameStates.InProgress) != 0) return false;
		if ((GameState & (GameStates.Normal | GameStates.Knockout)) != 0) return false;
		if ((GameState & GameStates.Multiplayer) == 0) return false;

		CPH.LogDebug(String.Format("{0}Knockout game type was chosen", LogPrefix));
		GameState = GameState | GameStates.Knockout;
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
		CPH.LogDebug(String.Format("{0}Multiplayer game announced", LogPrefix));
		var message = String.Format(config.multiplayerAnnouncement, user, MaxNumberOfPlayers, config.joinTimeout);
		SendMessage(commandSource, message);

		message = String.Format(config.joinTimeoutMessage, user);
		TimeoutTimer = TimerStart(commandSource, message, config.joinTimeout, OnJoinTimeout);
	}

	void StartDuel(string commandSource, string user)
	{
		CPH.LogDebug(String.Format("{0}Duel announced", LogPrefix));
		MaxNumberOfPlayers = 2;

		var message = String.Format(config.duelAnnouncement, user, config.joinTimeout);
		SendMessage(commandSource, message);

		message = String.Format(config.joinTimeoutMessage, user);
		TimeoutTimer = TimerStart(commandSource, message, config.joinTimeout, OnJoinTimeout);
	}

	public bool Join()
	{
		if ((GameState & GameStates.InProgress) != 0) return false;
		if ((GameState & (GameStates.Normal | GameStates.Knockout)) == 0) return false;

		string commandSource;
		CPH.TryGetArg<string>("userType", out commandSource);

		string user;
		switch(commandSource)
		{
			case "twitch":				
				CPH.TryGetArg<string>("user", out user);
				break;
			case "youtube":
				CPH.TryGetArg<string>("userName", out user);
				break;
			default:
				CPH.TryGetArg<string>("user", out user);
				break;
		}

		CPH.LogDebug(String.Format("{0}{1} trying to join", LogPrefix, user));
		if (Players.Contains(user))
		{
			AlreadyJoined(commandSource, user);
			return false;
		}

		if (!(PlayerIndex < MaxNumberOfPlayers - 1))
		{
			var message = String.Format(config.gameIsFullMessage, user);
			SendMessage(commandSource, message);
			TimerStop();
			Multiplayer();
			return false;
		}

		Joining(commandSource, user);

		if (Players.Count == MaxNumberOfPlayers) Multiplayer();

		return true;
	}

	void Multiplayer()
	{
		if ((GameState & GameStates.InProgress) != 0) return;

		GameState = GameState | GameStates.InProgress;

		TimerStop();

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
		CPH.LogDebug(String.Format("{0}Round: {1}, player {2}", LogPrefix, Round, user));

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
		CPH.LogDebug(String.Format("{0}{1} already joined", LogPrefix, user));
		var message = String.Format(config.alreadyJoinedMessage, user);
		SendMessage(source, message);
	}

	void Joining(string source, string user)
	{
		PlayerIndex++;
		Players.Add(user, source);

		CPH.LogDebug(String.Format("{0}{1} joined.", LogPrefix, user));

		var message = String.Format(config.joinedMessage, user);
		SendMessage(source, message);
	}

	bool RollHesitation()
	{
		var rnd = CPH.Between(1000, 5000);

		CPH.LogDebug(String.Format("{0}Hesitation roll {1}", LogPrefix, rnd));

		CPH.Wait(rnd);

		return rnd > 2000;
	}

	void NoHesitate(string commandSource, string user)
	{
		CPH.LogDebug(String.Format("{0}Player {1} doesn't hesitate", LogPrefix, user));

		var message = String.Format(config.noHesitationMessage, user);
		SendMessage(commandSource, message);

		CPH.Wait(500);
	}

	void Hesitate(string commandSource, string user)
	{
		CPH.LogDebug(String.Format("{0}Player {1} hesitates", LogPrefix, user));

		var message = String.Format(config.hesitationMessage, user);
		SendMessage(commandSource, message);

		CPH.Wait(500);
	}

	bool RollShot()
	{
		CPH.LogDebug(String.Format("{0}Shot roll. Round {1}, chambers left {2}", LogPrefix, Round, config.numberOfChambers));

		return CPH.Between(0, config.numberOfChambers - Round) == 0;
	}

	void Shot(string commandSource, string user)
	{
		CPH.LogDebug(String.Format("{0}Player {1} got shot", LogPrefix, user));

		CPH.PlaySound(config.shotSoundPath, 1.0f, true);

		var message = String.Format(config.shotMessage, user);
		SendMessage(commandSource, message);

		Timeout(commandSource, user);
	}

	void Miss(string commandSource, string user)
	{
		CPH.LogDebug(String.Format("{0}Player {1} missed", LogPrefix, user));

		CPH.PlaySound(config.missSoundPath, 1.0f, true);

		var message = String.Format(config.missMessage, user);
		SendMessage(commandSource, message);
	}

	void Timeout(string commandSource, string user)
	{
		string message = String.Format(config.timeoutMessageOther, user);
		int duration = config.timeoutDuration * 60;
		
		if (commandSource == "twitch")
		{
			message = String.Format(config.timeoutMessage, user);

			if(IsModerator(user))
			{
				CPH.TwitchTimeoutUser(user, duration, config.timeoutReason, false);
				RemodeTimer = RemodeTimeout(user, duration + 10);
			} else {
				CPH.TwitchTimeoutUser(user, duration, config.timeoutReason, config.twitchUseBotAccount);
			}
		}
		else if (commandSource == "youtube")
		{
				string broadcastId;
				CPH.TryGetArg("broadcast.id", out broadcastId);
				CPH.LogDebug($"broadcastId: {broadcastId}");
				CPH.YouTubeTimeoutUserByName(user, duration, broadcastId);
		}

		SendMessage(commandSource, message);
	}
	
	Timer RemodeTimeout(string user, int duration)
	{		
		CPH.LogDebug(String.Format("{0}{1} second timer started", LogPrefix, duration));
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
		CPH.LogDebug($"userInfo.IsModerator: {userInfo.IsModerator}");
		return userInfo.IsModerator;
	}

	void SendMessage(string source, string message)
	{
		if (source == "twitch")
			CPH.SendMessage(message, config.twitchUseBotAccount);
		else
			CPH.SendYouTubeMessage(message);
	}

	Timer TimerStart(string commandSource, string message, int duration, Func<string, string, ElapsedEventHandler> timeoutFunc)
	{
		CPH.LogDebug(String.Format("{0}{1} second timer started", LogPrefix, duration));
		var timer = new System.Timers.Timer(duration * 1000);
		timer.Elapsed += timeoutFunc(commandSource, message);
		timer.AutoReset = false;
		timer.Enabled = true;
		return timer;
	}

	void TimerStop()
	{
		CPH.LogDebug(String.Format("{0}Timer stopped", LogPrefix));
		TimeoutTimer.Stop();
		TimeoutTimer.Dispose();
	}

	ElapsedEventHandler OnRemodTimeout(string user)
	{
		return (Object source, System.Timers.ElapsedEventArgs e) =>
		{
			CPH.LogDebug(String.Format("{0}Remod timer for {1} triggered", LogPrefix, user));
			CPH.TwitchAddModerator(user);
			RemodeTimer.Stop();
			RemodeTimer.Dispose();
		};
	}

	ElapsedEventHandler OnStartTimeout(string commandSource, string message)
	{
		return (Object source, System.Timers.ElapsedEventArgs e) =>
		{
			CPH.LogDebug(String.Format("{0}Start timeout timer triggered", LogPrefix));
			SendMessage(commandSource, message);
			if (IsChannelPointReward()) RefundPoints(rewardId, redemptionId);
			Reset();
		};
	}

	ElapsedEventHandler OnTypeTimeout(string commandSource, string message)
	{
		return (Object source, System.Timers.ElapsedEventArgs e) =>
		{
			CPH.LogDebug(String.Format("{0}Type timeout timer triggered", LogPrefix));
			SendMessage(commandSource, message);
			StartNormal();
		};
	}

	ElapsedEventHandler OnJoinTimeout(string commandSource, string message)
	{
		return (Object source, System.Timers.ElapsedEventArgs e) =>
		{
			CPH.LogDebug(String.Format("{0}Join timeout timer triggered", LogPrefix));
			if (Players.Count > 2)
			{
				Multiplayer();
				return;
			}

			if (IsChannelPointReward()) RefundPoints(rewardId, redemptionId);
			SendMessage(commandSource, message);
			Reset();
		};
	}
}

class RouletteConfig()
{
	public string barrelSoundPath { get; set; }
	public string shotSoundPath { get; set; }
	public string missSoundPath { get; set; }
	public bool singlePlayerMode {get; set; } = false;
	public int numberOfChambers { get; set; }
	public int startTimeout { get; set; }
	public int typeTimeout { get; set; }
	public int joinTimeout { get; set; }
	public int timeoutDuration { get; set; }
	public bool twitchUseBotAccount { get; set; }
	public string timeoutReason { get; set; }

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
	public string timeoutMessage { get; set; }
	public string timeoutMessageOther { get; set; }
}