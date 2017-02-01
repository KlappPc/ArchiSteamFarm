﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2017 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.JSON;
using ArchiSteamFarm.Localization;
using Newtonsoft.Json;
using SteamKit2;
using SteamKit2.Discovery;
using SteamKit2.Internal;

namespace ArchiSteamFarm {
	internal sealed class Bot : IDisposable {
		private const ushort CallbackSleep = 500; // In miliseconds
		private const byte FamilySharingInactivityMinutes = 5;
		private const byte LoginCooldownInMinutes = 25; // Captcha disappears after around 20 minutes, so we make it 25
		private const uint LoginID = GlobalConfig.DefaultWCFPort; // This must be the same for all ASF bots and all ASF processes
		private const ushort MaxSteamMessageLength = 2048;
		private const byte MaxTwoFactorCodeFailures = 3;
		private const byte MinHeartBeatTTL = GlobalConfig.DefaultConnectionTimeout; // Assume client is responsive for at least that amount of seconds

		internal static readonly ConcurrentDictionary<string, Bot> Bots = new ConcurrentDictionary<string, Bot>();

		private static readonly SemaphoreSlim GiftsSemaphore = new SemaphoreSlim(1);
		private static readonly SemaphoreSlim LoginSemaphore = new SemaphoreSlim(1);

		internal readonly ArchiHandler ArchiHandler;
		internal readonly ArchiLogger ArchiLogger;
		internal readonly ArchiWebHandler ArchiWebHandler;
		internal readonly string BotName;

		internal bool HasMobileAuthenticator => BotDatabase?.MobileAuthenticator != null;
		internal bool IsConnectedAndLoggedOn => (SteamClient?.IsConnected == true) && (SteamClient.SteamID != null);
		internal bool IsPlayingPossible => !PlayingBlocked && (LibraryLockedBySteamID == 0);

		[JsonProperty]
		internal ulong SteamID => SteamClient?.SteamID ?? 0;

		private readonly BotDatabase BotDatabase;
		private readonly CallbackManager CallbackManager;
		private readonly SemaphoreSlim CallbackSemaphore = new SemaphoreSlim(1);

		[JsonProperty]
		private readonly CardsFarmer CardsFarmer;

		private readonly ConcurrentHashSet<ulong> HandledGifts = new ConcurrentHashSet<ulong>();
		private readonly Timer HeartBeatTimer;
		private readonly SemaphoreSlim InitializationSemaphore = new SemaphoreSlim(1);
		private readonly ConcurrentHashSet<uint> OwnedPackageIDs = new ConcurrentHashSet<uint>();

		private readonly string SentryFile;
		private readonly Statistics Statistics;
		private readonly SteamApps SteamApps;
		private readonly SteamClient SteamClient;
		private readonly ConcurrentHashSet<ulong> SteamFamilySharingIDs = new ConcurrentHashSet<ulong>();
		private readonly SteamFriends SteamFriends;
		//private readonly SteamSaleEvent SteamSaleEvent;
		private readonly SteamUser SteamUser;
		private readonly Trading Trading;

		[JsonProperty]
		internal BotConfig BotConfig { get; private set; }

		[JsonProperty]
		internal bool IsLimitedUser { get; private set; }

		[JsonProperty]
		internal bool KeepRunning { get; private set; }

		private Timer AcceptConfirmationsTimer;
		private string AuthCode;
		private Timer ConnectionFailureTimer;
		private Timer FamilySharingInactivityTimer;
		private bool FirstTradeSent;
		private byte HeartBeatFailures;
		private EResult LastLogOnResult;

        private List<uint> GamesReady = new List<uint>();
        Dictionary<uint, uint> GamesMixed = new Dictionary<uint, uint>();

        public void addGameReady(uint gameid) {
            if (GamesMixed.ContainsKey(gameid) || GamesReady.Contains(gameid)) return;

            GamesReady.Add(gameid);
        }

        public void addGameMixed(uint gameid, uint maxCards) {
            uint val;
            if (GamesMixed.TryGetValue(gameid, out val) && val == maxCards) return;
            if (GamesReady.Contains(gameid)) {
                GamesReady.Remove(gameid);
            }

            GamesMixed.Add(gameid, maxCards);
        }
		private ulong LibraryLockedBySteamID;
		private bool LootingAllowed = true;
		private bool PlayingBlocked;
		private Timer SendItemsTimer;
		private bool SkipFirstShutdown;
		private string TwoFactorCode;
		private byte TwoFactorCodeFailures;

		internal Bot(string botName) {
			if (string.IsNullOrEmpty(botName)) {
				throw new ArgumentNullException(nameof(botName));
			}

			if (Bots.ContainsKey(botName)) {
				throw new ArgumentException(string.Format(Strings.ErrorIsInvalid, nameof(botName)));
			}

			BotName = botName;
			ArchiLogger = new ArchiLogger(botName);

			string botPath = Path.Combine(SharedInfo.ConfigDirectory, botName);
			string botConfigFile = botPath + ".json";

			SentryFile = botPath + ".bin";

			BotConfig = BotConfig.Load(botConfigFile);
			if (BotConfig == null) {
				ArchiLogger.LogGenericError(string.Format(Strings.ErrorBotConfigInvalid, botConfigFile));
				return;
			}

			string botDatabaseFile = botPath + ".db";

			BotDatabase = BotDatabase.Load(botDatabaseFile);
			if (BotDatabase == null) {
				ArchiLogger.LogGenericError(string.Format(Strings.ErrorDatabaseInvalid, botDatabaseFile));
				return;
			}

			// Register bot as available for ASF
			if (!Bots.TryAdd(botName, this)) {
				throw new ArgumentException(string.Format(Strings.ErrorIsInvalid, nameof(botName)));
			}

			if (HasMobileAuthenticator) {
				BotDatabase.MobileAuthenticator.Init(this);
			} else {
				// Support and convert SDA files
				string maFilePath = botPath + ".maFile";
				if (File.Exists(maFilePath)) {
					ImportAuthenticator(maFilePath);
				}
			}

			// Initialize
			SteamClient = new SteamClient(Program.GlobalConfig.SteamProtocol);

			if (Program.GlobalConfig.Debug && Directory.Exists(SharedInfo.DebugDirectory)) {
				string debugListenerPath = Path.Combine(SharedInfo.DebugDirectory, botName);

				try {
					Directory.CreateDirectory(debugListenerPath);
					SteamClient.DebugNetworkListener = new NetHookNetworkListener(debugListenerPath);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);
				}
			}

			ArchiHandler = new ArchiHandler(ArchiLogger);
			SteamClient.AddHandler(ArchiHandler);

			CallbackManager = new CallbackManager(SteamClient);
			CallbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
			CallbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);

			SteamApps = SteamClient.GetHandler<SteamApps>();
			CallbackManager.Subscribe<SteamApps.FreeLicenseCallback>(OnFreeLicense);
			CallbackManager.Subscribe<SteamApps.GuestPassListCallback>(OnGuestPassList);
			CallbackManager.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
			CallbackManager.Subscribe<SteamApps.PICSProductInfoCallback>(OnPICSProductInfo);

			SteamFriends = SteamClient.GetHandler<SteamFriends>();
			CallbackManager.Subscribe<SteamFriends.ChatInviteCallback>(OnChatInvite);
			CallbackManager.Subscribe<SteamFriends.ChatMsgCallback>(OnChatMsg);
			CallbackManager.Subscribe<SteamFriends.FriendsListCallback>(OnFriendsList);
			CallbackManager.Subscribe<SteamFriends.FriendMsgCallback>(OnFriendMsg);
			CallbackManager.Subscribe<SteamFriends.FriendMsgHistoryCallback>(OnFriendMsgHistory);
			CallbackManager.Subscribe<SteamFriends.PersonaStateCallback>(OnPersonaState);

			SteamUser = SteamClient.GetHandler<SteamUser>();
			CallbackManager.Subscribe<SteamUser.AccountInfoCallback>(OnAccountInfo);
			CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
			CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
			CallbackManager.Subscribe<SteamUser.LoginKeyCallback>(OnLoginKey);
			CallbackManager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
			CallbackManager.Subscribe<SteamUser.WebAPIUserNonceCallback>(OnWebAPIUserNonce);

			CallbackManager.Subscribe<ArchiHandler.NotificationsCallback>(OnNotifications);
			CallbackManager.Subscribe<ArchiHandler.OfflineMessageCallback>(OnOfflineMessage);
			CallbackManager.Subscribe<ArchiHandler.PlayingSessionStateCallback>(OnPlayingSessionState);
			CallbackManager.Subscribe<ArchiHandler.PurchaseResponseCallback>(OnPurchaseResponse);
			CallbackManager.Subscribe<ArchiHandler.SharedLibraryLockStatusCallback>(OnSharedLibraryLockStatus);

			ArchiWebHandler = new ArchiWebHandler(this);

			CardsFarmer = new CardsFarmer(this);
			CardsFarmer.SetInitialState(BotConfig.Paused);

			//SteamSaleEvent = new SteamSaleEvent(this);
			Trading = new Trading(this);

			if (!Debugging.IsDebugBuild && Program.GlobalConfig.Statistics) {
				Statistics = new Statistics(this);
			}

			HeartBeatTimer = new Timer(
				async e => await HeartBeat().ConfigureAwait(false),
				null,
				TimeSpan.FromMinutes(1) + TimeSpan.FromMinutes(0.2 * Bots.Count), // Delay
				TimeSpan.FromMinutes(1) // Period
			);

			Initialize().Forget();
		}

		public void Dispose() {
			// Those are objects that are always being created if constructor doesn't throw exception
			ArchiWebHandler.Dispose();
			CardsFarmer.Dispose();
			HeartBeatTimer.Dispose();
			HandledGifts.Dispose();
			CallbackSemaphore.Dispose();
			InitializationSemaphore.Dispose();
			SteamFamilySharingIDs.Dispose();
			OwnedPackageIDs.Dispose();
			//SteamSaleEvent.Dispose();
			Trading.Dispose();

			// Those are objects that might be null and the check should be in-place
			AcceptConfirmationsTimer?.Dispose();
			ConnectionFailureTimer?.Dispose();
			FamilySharingInactivityTimer?.Dispose();
			SendItemsTimer?.Dispose();
			Statistics?.Dispose();
		}

		internal async Task<bool> AcceptConfirmations(bool accept, Steam.ConfirmationDetails.EType acceptedType = Steam.ConfirmationDetails.EType.Unknown, ulong acceptedSteamID = 0, HashSet<ulong> acceptedTradeIDs = null) {
			if (!HasMobileAuthenticator) {
				return false;
			}

			while (true) {
				HashSet<MobileAuthenticator.Confirmation> confirmations = await BotDatabase.MobileAuthenticator.GetConfirmations().ConfigureAwait(false);
				if ((confirmations == null) || (confirmations.Count == 0)) {
					return true;
				}

				if (acceptedType != Steam.ConfirmationDetails.EType.Unknown) {
					if (confirmations.RemoveWhere(confirmation => (confirmation.Type != acceptedType) && (confirmation.Type != Steam.ConfirmationDetails.EType.Other)) > 0) {
						if (confirmations.Count == 0) {
							return true;
						}
					}
				}

				if ((acceptedSteamID == 0) && ((acceptedTradeIDs == null) || (acceptedTradeIDs.Count == 0))) {
					if (!await BotDatabase.MobileAuthenticator.HandleConfirmations(confirmations, accept).ConfigureAwait(false)) {
						return false;
					}

					continue;
				}

				ICollection<Steam.ConfirmationDetails> results;
				IEnumerable<Task<Steam.ConfirmationDetails>> tasks = confirmations.Select(BotDatabase.MobileAuthenticator.GetConfirmationDetails);

				switch (Program.GlobalConfig.OptimizationMode) {
					case GlobalConfig.EOptimizationMode.MinMemoryUsage:
						results = new List<Steam.ConfirmationDetails>(confirmations.Count);
						foreach (Task<Steam.ConfirmationDetails> task in tasks) {
							results.Add(await task.ConfigureAwait(false));
						}

						break;
					default:
						results = await Task.WhenAll(tasks).ConfigureAwait(false);
						break;
				}

				HashSet<MobileAuthenticator.Confirmation> ignoredConfirmations = new HashSet<MobileAuthenticator.Confirmation>(results.Where(details => (details != null) && (((acceptedSteamID != 0) && (details.OtherSteamID64 != 0) && (acceptedSteamID != details.OtherSteamID64)) || ((acceptedTradeIDs != null) && (details.TradeOfferID != 0) && !acceptedTradeIDs.Contains(details.TradeOfferID)))).Select(details => details.Confirmation));

				if (ignoredConfirmations.Count > 0) {
					confirmations.ExceptWith(ignoredConfirmations);
					if (confirmations.Count == 0) {
						return true;
					}
				}

				if (!await BotDatabase.MobileAuthenticator.HandleConfirmations(confirmations, accept).ConfigureAwait(false)) {
					return false;
				}
			}
		}

		internal static string GetAPIStatus() {
			var response = new {
				Bots
			};

			try {
				return JsonConvert.SerializeObject(response);
			} catch (JsonException e) {
				ASF.ArchiLogger.LogGenericException(e);
				return null;
			}
		}

		internal static async Task InitializeCMs(uint cellID, IServerListProvider serverListProvider) {
			if (serverListProvider == null) {
				ASF.ArchiLogger.LogNullError(nameof(serverListProvider));
				return;
			}

			CMClient.Servers.CellID = cellID;
			CMClient.Servers.ServerListProvider = serverListProvider;

			// Normally we wouldn't need to do this, but there is a case where our list might be invalid or outdated
			// Ensure that we always ask once for list of up-to-date servers, even if we have list saved
			ASF.ArchiLogger.LogGenericInfo(string.Format(Strings.Initializing, nameof(SteamDirectory)));

			try {
				await SteamDirectory.Initialize(cellID).ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericInfo(Strings.Success);
			} catch {
				ASF.ArchiLogger.LogGenericWarning(Strings.BotSteamDirectoryInitializationFailed);
			}
		}

		internal async Task<bool?> IsReleased(uint appID) {
			if (appID == 0) {
				ArchiLogger.LogNullError(nameof(appID));
				return null;
			}

			AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet productInfo;

			try {
				productInfo = await SteamApps.PICSGetProductInfo(appID, null);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return null;
			}

			foreach (string releaseState in productInfo.Results.SelectMany(productResult => productResult.Apps).Where(app => appID == app.Key).Select(app => app.Value.KeyValues["common"]["ReleaseState"].Value).Where(releaseState => !string.IsNullOrEmpty(releaseState))) {
				switch (releaseState) {
					case "released":
						return true;
					case "prerelease":
					case "preloadonly":
						return false;
					default:
						ArchiLogger.LogGenericWarning(string.Format(Strings.WarningUnknownValuePleaseReport, nameof(releaseState), releaseState));
						return null;
				}
			}

			return null;
		}

		internal async Task LootIfNeeded() {
			if (!BotConfig.SendOnFarmingFinished || (BotConfig.SteamMasterID == 0) || !IsConnectedAndLoggedOn || (BotConfig.SteamMasterID == SteamClient.SteamID)) {
				return;
			}

			await ResponseLoot(BotConfig.SteamMasterID).ConfigureAwait(false);
		}

		internal async Task OnFarmingFinished(bool farmedSomething) {
			OnFarmingStopped();

			if (farmedSomething || !FirstTradeSent) {
				FirstTradeSent = true;
				await LootIfNeeded().ConfigureAwait(false);
			}

			if (BotConfig.ShutdownOnFarmingFinished) {
				if (SkipFirstShutdown) {
					SkipFirstShutdown = false;
				} else {
					Stop();
				}
			}
		}

		internal void OnFarmingStopped() => ResetGamesPlayed();

		internal async Task OnNewConfigLoaded(ASF.BotConfigEventArgs args) {
			if (args == null) {
				ArchiLogger.LogNullError(nameof(args));
				return;
			}

			if (args.BotConfig == null) {
				Destroy();
				return;
			}

			if (args.BotConfig == BotConfig) {
				return;
			}

			await InitializationSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (args.BotConfig == BotConfig) {
					return;
				}

				Stop(false);
				BotConfig = args.BotConfig;

				CardsFarmer.SetInitialState(BotConfig.Paused);

				if (BotConfig.AcceptConfirmationsPeriod > 0) {
					TimeSpan delay = TimeSpan.FromMinutes(BotConfig.AcceptConfirmationsPeriod) + TimeSpan.FromMinutes(0.2 * Bots.Count);
					TimeSpan period = TimeSpan.FromMinutes(BotConfig.AcceptConfirmationsPeriod);

					if (AcceptConfirmationsTimer == null) {
						AcceptConfirmationsTimer = new Timer(
							async e => await AcceptConfirmations(true).ConfigureAwait(false),
							null,
							delay, // Delay
							period // Period
						);
					} else {
						AcceptConfirmationsTimer.Change(delay, period);
					}
				} else if (AcceptConfirmationsTimer != null) {
					AcceptConfirmationsTimer.Dispose();
					AcceptConfirmationsTimer = null;
				}

				if ((BotConfig.SendTradePeriod > 0) && (BotConfig.SteamMasterID != 0)) {
					TimeSpan delay = TimeSpan.FromHours(BotConfig.SendTradePeriod) + TimeSpan.FromMinutes(Bots.Count);
					TimeSpan period = TimeSpan.FromHours(BotConfig.SendTradePeriod);

					if (SendItemsTimer == null) {
						SendItemsTimer = new Timer(
							async e => await ResponseLoot(BotConfig.SteamMasterID).ConfigureAwait(false),
							null,
							delay, // Delay
							period // Period
						);
					} else {
						SendItemsTimer.Change(delay, period);
					}
				} else if (SendItemsTimer != null) {
					SendItemsTimer.Dispose();
					SendItemsTimer = null;
				}

				await Initialize().ConfigureAwait(false);
			} finally {
				InitializationSemaphore.Release();
			}
		}

		internal async Task<bool> RefreshSession() {
			if (!IsConnectedAndLoggedOn) {
				return false;
			}

			SteamUser.WebAPIUserNonceCallback callback;

			try {
				callback = await SteamUser.RequestWebAPIUserNonce();
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				await Connect(true).ConfigureAwait(false);
				return false;
			}

			if (string.IsNullOrEmpty(callback?.Nonce)) {
				await Connect(true).ConfigureAwait(false);
				return false;
			}

			if (await ArchiWebHandler.Init(SteamClient.SteamID, SteamClient.ConnectedUniverse, callback.Nonce, BotConfig.SteamParentalPIN).ConfigureAwait(false)) {
				return true;
			}

			await Connect(true).ConfigureAwait(false);
			return false;
		}

		internal async Task<string> Response(ulong steamID, string message) {
			if ((steamID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(message));
				return null;
			}

			if (message[0] != '!') {
				if (!IsMaster(steamID)) {
					return null;
				}

				return await ResponseRedeem(steamID, message, ERedeemFlags.Validate).ConfigureAwait(false);
			}

			if (message.IndexOf(' ') < 0) {
				switch (message.ToUpperInvariant()) {
					case "!2FA":
						return await Response2FA(steamID).ConfigureAwait(false);
					case "!2FANO":
						return await Response2FAConfirm(steamID, false).ConfigureAwait(false);
					case "!2FAOK":
						return await Response2FAConfirm(steamID, true).ConfigureAwait(false);
					case "!API":
						return ResponseAPI(steamID);
					case "!EXIT":
						return ResponseExit(steamID);
					case "!FARM":
						return await ResponseFarm(steamID).ConfigureAwait(false);
					case "!HELP":
						return ResponseHelp(steamID);
					case "!LOOT":
						return await ResponseLoot(steamID).ConfigureAwait(false);
					case "!LOOT^":
						return ResponseLootSwitch(steamID);
					case "!PASSWORD":
						return ResponsePassword(steamID);
					case "!PAUSE":
						return await ResponsePause(steamID, false).ConfigureAwait(false);
					case "!PAUSE^":
						return await ResponsePause(steamID, true).ConfigureAwait(false);
					case "!REJOINCHAT":
						return ResponseRejoinChat(steamID);
					case "!RESUME":
						return ResponseResume(steamID);
					case "!RESTART":
						return ResponseRestart(steamID);
					case "!STATUS":
						return ResponseStatus(steamID);
					case "!STOP":
						return ResponseStop(steamID);
					case "!UPDATE":
						return await ResponseUpdate(steamID).ConfigureAwait(false);
					case "!VERSION":
						return ResponseVersion(steamID);
					default:
						return ResponseUnknown(steamID);
				}
			}

			string[] args = message.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
			switch (args[0].ToUpperInvariant()) {
				case "!2FA":
					return await Response2FA(steamID, args[1]).ConfigureAwait(false);
				case "!2FANO":
					return await Response2FAConfirm(steamID, args[1], false).ConfigureAwait(false);
				case "!2FAOK":
					return await Response2FAConfirm(steamID, args[1], true).ConfigureAwait(false);
				case "!ADDLICENSE":
					if (args.Length > 2) {
						return await ResponseAddLicense(steamID, args[1], args[2]).ConfigureAwait(false);
					}

					return await ResponseAddLicense(steamID, args[1]).ConfigureAwait(false);
				case "!FARM":
					return await ResponseFarm(steamID, args[1]).ConfigureAwait(false);
				case "!LOOT":
					return await ResponseLoot(steamID, args[1]).ConfigureAwait(false);
				case "!LOOT^":
					return await ResponseLootSwitch(steamID, args[1]).ConfigureAwait(false);
				case "!OWNS":
					if (args.Length > 2) {
						return await ResponseOwns(steamID, args[1], args[2]).ConfigureAwait(false);
					}
					return await ResponseOwns(steamID, args[1]).ConfigureAwait(false);
                case "!TRANSFER":
                    if (args.Length <= 3) {
                        return ResponseUnknown(steamID); //think of how to handle missing info.
                    }
                    return await ResponseTransfer(steamID, args[1], args[2], args[3]).ConfigureAwait(false);
				case "!PASSWORD":
					return await ResponsePassword(steamID, args[1]).ConfigureAwait(false);
				case "!PAUSE":
					return await ResponsePause(steamID, args[1], false).ConfigureAwait(false);
				case "!PAUSE^":
					return await ResponsePause(steamID, args[1], true).ConfigureAwait(false);
				case "!PLAY":
					if (args.Length > 2) {
						return await ResponsePlay(steamID, args[1], args[2]).ConfigureAwait(false);
					}

					return await ResponsePlay(steamID, args[1]).ConfigureAwait(false);
				case "!R":
				case "!REDEEM":
					if (args.Length > 2) {
						return await ResponseRedeem(steamID, args[1], args[2]).ConfigureAwait(false);
					}

					return await ResponseRedeem(steamID, args[1]).ConfigureAwait(false);
				case "!R^":
				case "!REDEEM^":
					if (args.Length > 2) {
						return await ResponseRedeem(steamID, args[1], args[2], ERedeemFlags.SkipForwarding | ERedeemFlags.SkipDistribution).ConfigureAwait(false);
					}

					return await ResponseRedeem(steamID, args[1], ERedeemFlags.SkipForwarding | ERedeemFlags.SkipDistribution).ConfigureAwait(false);
				case "!R&":
				case "!REDEEM&":
					if (args.Length > 2) {
						return await ResponseRedeem(steamID, args[1], args[2], ERedeemFlags.ForceForwarding | ERedeemFlags.SkipInitial).ConfigureAwait(false);
					}

					return await ResponseRedeem(steamID, args[1], ERedeemFlags.ForceForwarding | ERedeemFlags.SkipInitial).ConfigureAwait(false);
				case "!RESUME":
					return await ResponseResume(steamID, args[1]).ConfigureAwait(false);
				case "!START":
					return await ResponseStart(steamID, args[1]).ConfigureAwait(false);
				case "!STATUS":
					return await ResponseStatus(steamID, args[1]).ConfigureAwait(false);
				case "!STOP":
					return await ResponseStop(steamID, args[1]).ConfigureAwait(false);
				default:
					return ResponseUnknown(steamID);
			}
		}

		internal void SendMessage(ulong steamID, string message) {
			if ((steamID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(message));
				return;
			}

			if (new SteamID(steamID).IsChatAccount) {
				SendMessageToChannel(steamID, message);
			} else {
				SendMessageToUser(steamID, message);
			}
		}

		internal void Stop(bool withShutdownEvent = true) {
			if (!KeepRunning) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotStopping);
			KeepRunning = false;

			if (SteamClient.IsConnected) {
				Disconnect();
			}

			if (withShutdownEvent) {
				Events.OnBotShutdown();
			}
		}

		private void CheckFamilySharingInactivity() {
			if (!IsPlayingPossible) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotAutomaticIdlingPauseTimeout);
			StopFamilySharingInactivityTimer();
			CardsFarmer.Resume(false);
		}

		private void CheckOccupationStatus() {
			if (!IsPlayingPossible) {
				ArchiLogger.LogGenericInfo(Strings.BotAccountOccupied);
				StopFamilySharingInactivityTimer();
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotAccountFree);
			CardsFarmer.Resume(false);
		}

		private async Task Connect(bool force = false) {
			if (!force && (!KeepRunning || SteamClient.IsConnected)) {
				return;
			}

			// Use limiter only when user is not providing 2FA token by himself
			if (string.IsNullOrEmpty(TwoFactorCode)) {
				await LimitLoginRequestsAsync().ConfigureAwait(false);

				if (HasMobileAuthenticator) {
					// In this case, we can also use ASF 2FA for providing 2FA token, even if it's not required
					TwoFactorCode = await BotDatabase.MobileAuthenticator.GenerateToken().ConfigureAwait(false);
				}
			}

			if (!force && (!KeepRunning || SteamClient.IsConnected)) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotConnecting);
			InitConnectionFailureTimer();
			SteamClient.Connect();
		}

		private void Destroy(bool force = false) {
			if (!force) {
				Stop();
			} else {
				// Stop() will most likely block due to fuckup, don't wait for it
				Task.Run(() => Stop()).Forget();
			}

			Bot ignored;
			Bots.TryRemove(BotName, out ignored);
		}

		private void Disconnect() {
			StopConnectionFailureTimer();
			SteamClient.Disconnect();
		}

		private static HashSet<Bot> GetBots(string args) {
			if (string.IsNullOrEmpty(args)) {
				ASF.ArchiLogger.LogNullError(nameof(args));
				return null;
			}

			string[] botNames = args.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

			HashSet<Bot> result = new HashSet<Bot>();
			foreach (string botName in botNames.Where(botName => !string.IsNullOrEmpty(botName))) {
				if (botName.Equals(SharedInfo.ASF)) {
					foreach (Bot bot in Bots.OrderBy(bot => bot.Key).Select(bot => bot.Value)) {
						result.Add(bot);
					}

					return result;
				}

				Bot targetBot;
				if (!Bots.TryGetValue(botName, out targetBot)) {
					continue;
				}

				result.Add(targetBot);
			}

			return result;
		}

		private void HandleCallbacks() {
			TimeSpan timeSpan = TimeSpan.FromMilliseconds(CallbackSleep);
			while (KeepRunning || SteamClient.IsConnected) {
				if (!CallbackSemaphore.Wait(0)) {
					if (Debugging.IsUserDebugging) {
						ArchiLogger.LogGenericDebug(string.Format(Strings.WarningFailedWithError, nameof(CallbackSemaphore)));
					}

					return;
				}

				try {
					CallbackManager.RunWaitAllCallbacks(timeSpan);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);
				} finally {
					CallbackSemaphore.Release();
				}
			}
		}

		private async Task HandleMessage(ulong chatID, ulong steamID, string message) {
			if ((chatID == 0) || (steamID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(chatID) + " || " + nameof(steamID) + " || " + nameof(message));
				return;
			}

			string response = await Response(steamID, message).ConfigureAwait(false);

			// We respond with null when user is not authorized (and similar)
			if (string.IsNullOrEmpty(response)) {
				return;
			}

			SendMessage(chatID, response);
		}

		private async Task HeartBeat() {
			if (!KeepRunning || !IsConnectedAndLoggedOn || (HeartBeatFailures == byte.MaxValue)) {
				return;
			}

			try {
				if (DateTime.UtcNow.Subtract(ArchiHandler.LastPacketReceived).TotalSeconds > MinHeartBeatTTL) {
					await SteamApps.PICSGetProductInfo(0, null);
				}

				HeartBeatFailures = 0;
				Statistics?.OnHeartBeat().Forget();
			} catch (Exception e) {
				if (Debugging.IsUserDebugging) {
					ArchiLogger.LogGenericDebugException(e);
				}

				if (!KeepRunning || !IsConnectedAndLoggedOn || (HeartBeatFailures == byte.MaxValue)) {
					return;
				}

				if (++HeartBeatFailures >= (byte) Math.Ceiling(Program.GlobalConfig.ConnectionTimeout / 10.0)) {
					HeartBeatFailures = byte.MaxValue;
					ArchiLogger.LogGenericWarning(Strings.BotConnectionLost);
					Connect(true).Forget();
				}
			}
		}

		private void ImportAuthenticator(string maFilePath) {
			if (HasMobileAuthenticator || !File.Exists(maFilePath)) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotAuthenticatorConverting);

			try {
				BotDatabase.MobileAuthenticator = JsonConvert.DeserializeObject<MobileAuthenticator>(File.ReadAllText(maFilePath));
				File.Delete(maFilePath);
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return;
			}

			if (BotDatabase.MobileAuthenticator == null) {
				ArchiLogger.LogNullError(nameof(BotDatabase.MobileAuthenticator));
				return;
			}

			BotDatabase.MobileAuthenticator.Init(this);

			if (!BotDatabase.MobileAuthenticator.HasCorrectDeviceID) {
				ArchiLogger.LogGenericWarning(Strings.BotAuthenticatorInvalidDeviceID);
				string deviceID = Program.GetUserInput(ASF.EUserInputType.DeviceID, BotName);
				if (string.IsNullOrEmpty(deviceID)) {
					BotDatabase.MobileAuthenticator = null;
					return;
				}

				BotDatabase.MobileAuthenticator.CorrectDeviceID(deviceID);
				BotDatabase.Save();
			}

			ArchiLogger.LogGenericInfo(Strings.BotAuthenticatorImportFinished);
		}

		private void InitConnectionFailureTimer() {
			if (ConnectionFailureTimer != null) {
				return;
			}

			ConnectionFailureTimer = new Timer(
				e => InitPermanentConnectionFailure(),
				null,
				TimeSpan.FromMinutes(Math.Ceiling(Program.GlobalConfig.ConnectionTimeout / 30.0)), // Delay
				Timeout.InfiniteTimeSpan // Period
			);
		}

		private async Task Initialize() {
			if (!BotConfig.Enabled) {
				ArchiLogger.LogGenericInfo(Strings.BotInstanceNotStartingBecauseDisabled);
				return;
			}

			// Start
			await Start().ConfigureAwait(false);
		}

		private async Task InitializeFamilySharing() {
			HashSet<ulong> steamIDs = await ArchiWebHandler.GetFamilySharingSteamIDs().ConfigureAwait(false);
			if ((steamIDs == null) || (steamIDs.Count == 0)) {
				return;
			}

			SteamFamilySharingIDs.ReplaceIfNeededWith(steamIDs);
		}

		private bool InitializeLoginAndPassword(bool requiresPassword) {
			if (string.IsNullOrEmpty(BotConfig.SteamLogin)) {
				BotConfig.SteamLogin = Program.GetUserInput(ASF.EUserInputType.Login, BotName);
				if (string.IsNullOrEmpty(BotConfig.SteamLogin)) {
					return false;
				}
			}

			if (!string.IsNullOrEmpty(BotConfig.SteamPassword) || (!requiresPassword && !string.IsNullOrEmpty(BotDatabase.LoginKey))) {
				return true;
			}

			BotConfig.SteamPassword = Program.GetUserInput(ASF.EUserInputType.Password, BotName);
			return !string.IsNullOrEmpty(BotConfig.SteamPassword);
		}

		private void InitPermanentConnectionFailure() {
			if (!KeepRunning) {
				return;
			}

			ArchiLogger.LogGenericError(Strings.BotHeartBeatFailed);
			Destroy(true);
			new Bot(BotName).Forget();
		}

		private bool IsMaster(ulong steamID) {
			if (steamID != 0) {
				return (steamID == BotConfig.SteamMasterID) || IsOwner(steamID);
			}

			ArchiLogger.LogNullError(nameof(steamID));
			return false;
		}

		private bool IsMasterClanID(ulong steamID) {
			if (steamID != 0) {
				return steamID == BotConfig.SteamMasterClanID;
			}

			ArchiLogger.LogNullError(nameof(steamID));
			return false;
		}

		private static bool IsOwner(ulong steamID) {
			if (steamID != 0) {
				return (steamID == Program.GlobalConfig.SteamOwnerID) || (Debugging.IsDebugBuild && (steamID == SharedInfo.ArchiSteamID));
			}

			ASF.ArchiLogger.LogNullError(nameof(steamID));
			return false;
		}

		private static bool IsValidCdKey(string key) {
			if (!string.IsNullOrEmpty(key)) {
				return Regex.IsMatch(key, @"^[0-9A-Z]{4,7}-[0-9A-Z]{4,7}-[0-9A-Z]{4,7}(?:(?:-[0-9A-Z]{4,7})?(?:-[0-9A-Z]{4,7}))?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
			}

			ASF.ArchiLogger.LogNullError(nameof(key));
			return false;
		}

		private void JoinMasterChat() {
			if (!IsConnectedAndLoggedOn || (BotConfig.SteamMasterClanID == 0)) {
				return;
			}

			SteamFriends.JoinChat(BotConfig.SteamMasterClanID);
		}

		private static async Task LimitGiftsRequestsAsync() {
			await GiftsSemaphore.WaitAsync().ConfigureAwait(false);
			Task.Run(async () => {
				await Task.Delay(Program.GlobalConfig.GiftsLimiterDelay * 1000).ConfigureAwait(false);
				GiftsSemaphore.Release();
			}).Forget();
		}

		private static async Task LimitLoginRequestsAsync() {
			await LoginSemaphore.WaitAsync().ConfigureAwait(false);
			Task.Run(async () => {
				await Task.Delay(Program.GlobalConfig.LoginLimiterDelay * 1000).ConfigureAwait(false);
				LoginSemaphore.Release();
			}).Forget();
		}

		private void OnAccountInfo(SteamUser.AccountInfoCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			if (!BotConfig.FarmOffline) {
				SteamFriends.SetPersonaState(EPersonaState.Online);
			}
		}

		private void OnChatInvite(SteamFriends.ChatInviteCallback callback) {
			if ((callback?.ChatRoomID == null) || (callback.PatronID == null)) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.ChatRoomID) + " || " + nameof(callback.PatronID));
				return;
			}

			if (!IsMaster(callback.PatronID)) {
				return;
			}

			SteamFriends.JoinChat(callback.ChatRoomID);
		}

		private async void OnChatMsg(SteamFriends.ChatMsgCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			if (callback.ChatMsgType != EChatEntryType.ChatMsg) {
				return;
			}

			if ((callback.ChatRoomID == null) || (callback.ChatterID == null) || string.IsNullOrEmpty(callback.Message)) {
				ArchiLogger.LogNullError(nameof(callback.ChatRoomID) + " || " + nameof(callback.ChatterID) + " || " + nameof(callback.Message));
				return;
			}

			ArchiLogger.LogGenericTrace(callback.ChatRoomID.ConvertToUInt64() + "/" + callback.ChatterID.ConvertToUInt64() + ": " + callback.Message);

			switch (callback.Message.ToUpperInvariant()) {
				case "!LEAVE":
					if (!IsMaster(callback.ChatterID)) {
						break;
					}

					SteamFriends.LeaveChat(callback.ChatRoomID);
					break;
				default:
					await HandleMessage(callback.ChatRoomID, callback.ChatterID, callback.Message).ConfigureAwait(false);
					break;
			}
		}

		private void OnConnected(SteamClient.ConnectedCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			HeartBeatFailures = 0;
			StopConnectionFailureTimer();

			if (callback.Result != EResult.OK) {
				ArchiLogger.LogGenericError(string.Format(Strings.BotUnableToConnect, callback.Result));
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotConnected);

			if (!KeepRunning) {
				ArchiLogger.LogGenericInfo(Strings.BotDisconnecting);
				Disconnect();
				return;
			}

			byte[] sentryFileHash = null;
			if (File.Exists(SentryFile)) {
				try {
					byte[] sentryFileContent = File.ReadAllBytes(SentryFile);
					sentryFileHash = SteamKit2.CryptoHelper.SHAHash(sentryFileContent);
				} catch (Exception e) {
					ArchiLogger.LogGenericException(e);
				}
			}

			if (!InitializeLoginAndPassword(false)) {
				Stop();
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotLoggingIn);

			string password = BotConfig.SteamPassword;
			if (!string.IsNullOrEmpty(password)) {
				// Steam silently ignores non-ASCII characters in password, we're going to do the same
				// Don't ask me why, I know it's stupid
				password = Regex.Replace(password, @"[^\u0000-\u007F]+", "");
			}

			// Decrypt login key if needed
			string loginKey = BotDatabase.LoginKey;
			if (!string.IsNullOrEmpty(loginKey) && (loginKey.Length > 19)) {
				loginKey = CryptoHelper.Decrypt(BotConfig.PasswordFormat, loginKey);
			}

			SteamUser.LogOnDetails logOnDetails = new SteamUser.LogOnDetails {
				AuthCode = AuthCode,
				CellID = Program.GlobalDatabase.CellID,
				LoginID = LoginID,
				LoginKey = loginKey,
				Password = password,
				SentryFileHash = sentryFileHash,
				ShouldRememberPassword = true,
				TwoFactorCode = TwoFactorCode,
				Username = BotConfig.SteamLogin
			};

			InitConnectionFailureTimer();

			try {
				SteamUser.LogOn(logOnDetails);
			} catch {
				// TODO: Remove me once https://github.com/SteamRE/SteamKit/issues/305 is fixed
				ArchiHandler.LogOnWithoutMachineID(logOnDetails);
			}
		}

		private async void OnDisconnected(SteamClient.DisconnectedCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			EResult lastLogOnResult = LastLogOnResult;
			LastLogOnResult = EResult.Invalid;
			HeartBeatFailures = 0;
			StopConnectionFailureTimer();

			ArchiLogger.LogGenericInfo(Strings.BotDisconnected);

			ArchiWebHandler.OnDisconnected();
			CardsFarmer.OnDisconnected();
			Trading.OnDisconnected();

			FirstTradeSent = false;
			HandledGifts.ClearAndTrim();

			// If we initiated disconnect, do not attempt to reconnect
			if (callback.UserInitiated) {
				return;
			}

			switch (lastLogOnResult) {
				case EResult.Invalid:
					// Invalid means that we didn't get OnLoggedOn() in the first place, so Steam is down
					// Always reset one-time-only access tokens in this case, as OnLoggedOn() didn't do that for us
					AuthCode = TwoFactorCode = null;
					break;
				case EResult.InvalidPassword:
					// If we didn't use login key, it's nearly always rate limiting
					if (string.IsNullOrEmpty(BotDatabase.LoginKey)) {
						goto case EResult.RateLimitExceeded;
					}

					BotDatabase.LoginKey = null;
					ArchiLogger.LogGenericInfo(Strings.BotRemovedExpiredLoginKey);
					break;
				case EResult.NoConnection:
				case EResult.ServiceUnavailable:
				case EResult.Timeout:
				case EResult.TryAnotherCM:
					await Task.Delay(5000).ConfigureAwait(false);
					break;
				case EResult.RateLimitExceeded:
					ArchiLogger.LogGenericInfo(string.Format(Strings.BotRateLimitExceeded, LoginCooldownInMinutes));
					await Task.Delay(LoginCooldownInMinutes * 60 * 1000).ConfigureAwait(false);
					break;
			}

			if (!KeepRunning || SteamClient.IsConnected) {
				return;
			}

			ArchiLogger.LogGenericInfo(Strings.BotReconnecting);
			await Connect().ConfigureAwait(false);
		}

		private void OnFreeLicense(SteamApps.FreeLicenseCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
			}
		}

		private async void OnFriendMsg(SteamFriends.FriendMsgCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			if (callback.EntryType != EChatEntryType.ChatMsg) {
				return;
			}

			if ((callback.Sender == null) || string.IsNullOrEmpty(callback.Message)) {
				ArchiLogger.LogNullError(nameof(callback.Sender) + " || " + nameof(callback.Message));
				return;
			}

			ArchiLogger.LogGenericTrace(callback.Sender.ConvertToUInt64() + ": " + callback.Message);

			await HandleMessage(callback.Sender, callback.Sender, callback.Message).ConfigureAwait(false);
		}

		private async void OnFriendMsgHistory(SteamFriends.FriendMsgHistoryCallback callback) {
			if ((callback?.Messages == null) || (callback.SteamID == null)) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.Messages) + " || " + nameof(callback.SteamID));
				return;
			}

			if (callback.Messages.Count == 0) {
				return;
			}

			// Get last message
			SteamFriends.FriendMsgHistoryCallback.FriendMessage lastMessage = callback.Messages[callback.Messages.Count - 1];

			// If message is read already, return
			if (!lastMessage.Unread) {
				return;
			}

			// If message is too old, return
			if (DateTime.UtcNow.Subtract(lastMessage.Timestamp).TotalHours > 1) {
				return;
			}

			// Handle the message
			await HandleMessage(callback.SteamID, callback.SteamID, lastMessage.Message).ConfigureAwait(false);
		}

		private void OnFriendsList(SteamFriends.FriendsListCallback callback) {
			if (callback?.FriendList == null) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.FriendList));
				return;
			}

			foreach (SteamFriends.FriendsListCallback.Friend friend in callback.FriendList.Where(friend => friend.Relationship == EFriendRelationship.RequestRecipient)) {
				if (friend.SteamID.AccountType == EAccountType.Clan) {
					if (IsMasterClanID(friend.SteamID)) {
						ArchiHandler.AcceptClanInvite(friend.SteamID, true);
					} else if (BotConfig.IsBotAccount) {
						ArchiHandler.AcceptClanInvite(friend.SteamID, false);
					}
				} else {
					if (IsMaster(friend.SteamID)) {
						SteamFriends.AddFriend(friend.SteamID);
					} else if (BotConfig.IsBotAccount) {
						SteamFriends.RemoveFriend(friend.SteamID);
					}
				}
			}
		}

		private async void OnGuestPassList(SteamApps.GuestPassListCallback callback) {
			if (callback?.GuestPasses == null) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.GuestPasses));
				return;
			}

			if ((callback.CountGuestPassesToRedeem == 0) || (callback.GuestPasses.Count == 0) || !BotConfig.AcceptGifts) {
				return;
			}

			foreach (ulong gid in callback.GuestPasses.Select(guestPass => guestPass["gid"].AsUnsignedLong()).Where(gid => (gid != 0) && !HandledGifts.Contains(gid))) {
				HandledGifts.Add(gid);

				ArchiLogger.LogGenericInfo(string.Format(Strings.BotAcceptingGift, gid));
				await LimitGiftsRequestsAsync().ConfigureAwait(false);

				ArchiHandler.RedeemGuestPassResponseCallback response = await ArchiHandler.RedeemGuestPass(gid).ConfigureAwait(false);
				if (response != null) {
					if (response.Result == EResult.OK) {
						ArchiLogger.LogGenericInfo(Strings.Success);
					} else {
						ArchiLogger.LogGenericWarning(string.Format(Strings.WarningFailedWithError, response.Result));
					}
				} else {
					ArchiLogger.LogGenericWarning(Strings.WarningFailed);
				}
			}
		}

		private async void OnLicenseList(SteamApps.LicenseListCallback callback) {
			if (callback?.LicenseList == null) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.LicenseList));
				return;
			}

			HashSet<uint> ownedPackageIDs = new HashSet<uint>(callback.LicenseList.Select(license => license.PackageID));
			OwnedPackageIDs.ReplaceIfNeededWith(ownedPackageIDs);

			await Task.Delay(1000).ConfigureAwait(false); // Wait a second for eventual PlayingSessionStateCallback or SharedLibraryLockStatusCallback

			if (!ArchiWebHandler.Ready) {
				for (byte i = 0; (i < Program.GlobalConfig.ConnectionTimeout) && !ArchiWebHandler.Ready; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (!ArchiWebHandler.Ready) {
					return;
				}
			}

			// Normally we ResetGamesPlayed() in OnFarmingStopped() but there is no farming event if CardsFarmer module is disabled
			// Therefore, trigger extra ResetGamesPlayed(), but only in this specific case
			if (CardsFarmer.Paused) {
				ResetGamesPlayed();
			}

			// We trigger OnNewGameAdded() anyway, as CardsFarmer has other things to handle regardless of being Paused or not
			await CardsFarmer.OnNewGameAdded().ConfigureAwait(false);
		}

		private void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			ArchiLogger.LogGenericInfo(string.Format(Strings.BotLoggedOff, callback.Result));

			switch (callback.Result) {
				case EResult.LogonSessionReplaced:
					ArchiLogger.LogGenericError(Strings.BotLogonSessionReplaced);
					Stop();
					break;
			}
		}

		private async void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			// Always reset one-time-only access tokens
			AuthCode = TwoFactorCode = null;

			// Keep LastLogOnResult for OnDisconnected()
			LastLogOnResult = callback.Result;

			HeartBeatFailures = 0;
			StopConnectionFailureTimer();

			switch (callback.Result) {
				case EResult.AccountLogonDenied:
					AuthCode = Program.GetUserInput(ASF.EUserInputType.SteamGuard, BotName);
					if (string.IsNullOrEmpty(AuthCode)) {
						Stop();
					}

					break;
				case EResult.AccountLoginDeniedNeedTwoFactor:
					if (!HasMobileAuthenticator) {
						TwoFactorCode = Program.GetUserInput(ASF.EUserInputType.TwoFactorAuthentication, BotName);
						if (string.IsNullOrEmpty(TwoFactorCode)) {
							Stop();
						}
					}

					break;
				case EResult.OK:
					ArchiLogger.LogGenericInfo(Strings.BotLoggedOn);

					// Old status for these doesn't matter, we'll update them if needed
					LibraryLockedBySteamID = TwoFactorCodeFailures = 0;
					IsLimitedUser = PlayingBlocked = false;

					if (callback.AccountFlags.HasFlag(EAccountFlags.LimitedUser)) {
						IsLimitedUser = true;
						ArchiLogger.LogGenericWarning(Strings.BotAccountLimited);
					}

					if ((callback.CellID != 0) && (Program.GlobalDatabase.CellID != callback.CellID)) {
						Program.GlobalDatabase.CellID = callback.CellID;
					}

					if (!HasMobileAuthenticator) {
						// Support and convert SDA files
						string maFilePath = Path.Combine(SharedInfo.ConfigDirectory, callback.ClientSteamID.ConvertToUInt64() + ".maFile");
						if (File.Exists(maFilePath)) {
							ImportAuthenticator(maFilePath);
						}
					}

					if (string.IsNullOrEmpty(BotConfig.SteamParentalPIN)) {
						BotConfig.SteamParentalPIN = Program.GetUserInput(ASF.EUserInputType.SteamParentalPIN, BotName);
						if (string.IsNullOrEmpty(BotConfig.SteamParentalPIN)) {
							Stop();
							return;
						}
					}

					if (!await ArchiWebHandler.Init(callback.ClientSteamID, SteamClient.ConnectedUniverse, callback.WebAPIUserNonce, BotConfig.SteamParentalPIN).ConfigureAwait(false)) {
						if (!await RefreshSession().ConfigureAwait(false)) {
							return;
						}
					}

					// Sometimes Steam won't send us our own PersonaStateCallback, so request it explicitly
					SteamFriends.RequestFriendInfo(callback.ClientSteamID, EClientPersonaStateFlag.PlayerName | EClientPersonaStateFlag.Presence);

					InitializeFamilySharing().Forget();

					if (BotConfig.DismissInventoryNotifications) {
						ArchiWebHandler.MarkInventory().Forget();
					}

					if (BotConfig.SteamMasterClanID != 0) {
						Task.Run(async () => {
							await ArchiWebHandler.JoinGroup(BotConfig.SteamMasterClanID).ConfigureAwait(false);
							JoinMasterChat();
						}).Forget();
					}

					Statistics?.OnLoggedOn().Forget();
					Trading.CheckTrades().Forget();
					break;
				case EResult.InvalidPassword:
				case EResult.NoConnection:
				case EResult.RateLimitExceeded:
				case EResult.ServiceUnavailable:
				case EResult.Timeout:
				case EResult.TryAnotherCM:
				case EResult.TwoFactorCodeMismatch:
					ArchiLogger.LogGenericWarning(string.Format(Strings.BotUnableToLogin, callback.Result, callback.ExtendedResult));

					if ((callback.Result == EResult.TwoFactorCodeMismatch) && HasMobileAuthenticator) {
						if (++TwoFactorCodeFailures >= MaxTwoFactorCodeFailures) {
							TwoFactorCodeFailures = 0;
							ArchiLogger.LogGenericError(string.Format(Strings.BotInvalidAuthenticatorDuringLogin, MaxTwoFactorCodeFailures));
							Stop();
						}
					}

					break;
				default: // Unexpected result, shutdown immediately
					ArchiLogger.LogGenericError(string.Format(Strings.BotUnableToLogin, callback.Result, callback.ExtendedResult));
					Stop();
					break;
			}
		}

		private void OnLoginKey(SteamUser.LoginKeyCallback callback) {
			if (string.IsNullOrEmpty(callback?.LoginKey)) {
				ArchiLogger.LogNullError(nameof(callback) + " || " + nameof(callback.LoginKey));
				return;
			}

			string loginKey = callback.LoginKey;
			if (!string.IsNullOrEmpty(loginKey)) {
				loginKey = CryptoHelper.Encrypt(BotConfig.PasswordFormat, loginKey);
			}

			BotDatabase.LoginKey = loginKey;
			SteamUser.AcceptNewLoginKey(callback);
		}

		private void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			int fileSize;
			byte[] sentryHash;

			try {
				using (FileStream fileStream = File.Open(SentryFile, FileMode.OpenOrCreate, FileAccess.ReadWrite)) {
					fileStream.Seek(callback.Offset, SeekOrigin.Begin);
					fileStream.Write(callback.Data, 0, callback.BytesToWrite);
					fileSize = (int) fileStream.Length;

					fileStream.Seek(0, SeekOrigin.Begin);
					using (SHA1CryptoServiceProvider sha = new SHA1CryptoServiceProvider()) {
						sentryHash = sha.ComputeHash(fileStream);
					}
				}
			} catch (Exception e) {
				ArchiLogger.LogGenericException(e);
				return;
			}

			// Inform the steam servers that we're accepting this sentry file
			SteamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails {
				JobID = callback.JobID,
				FileName = callback.FileName,
				BytesWritten = callback.BytesToWrite,
				FileSize = fileSize,
				Offset = callback.Offset,
				Result = EResult.OK,
				LastError = 0,
				OneTimePassword = callback.OneTimePassword,
				SentryFileHash = sentryHash
			});
		}

		private void OnNotifications(ArchiHandler.NotificationsCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			if ((callback.Notifications == null) || (callback.Notifications.Count == 0)) {
				return;
			}

			foreach (ArchiHandler.NotificationsCallback.ENotification notification in callback.Notifications) {
				switch (notification) {
					case ArchiHandler.NotificationsCallback.ENotification.Items:
						CardsFarmer.OnNewItemsNotification().Forget();
						if (BotConfig.DismissInventoryNotifications) {
							ArchiWebHandler.MarkInventory().Forget();
						}
						break;
					case ArchiHandler.NotificationsCallback.ENotification.Trading:
						Trading.CheckTrades().Forget();
						break;
				}
			}
		}

		private void OnOfflineMessage(ArchiHandler.OfflineMessageCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			if ((callback.OfflineMessagesCount == 0) || !BotConfig.HandleOfflineMessages) {
				return;
			}

			SteamFriends.RequestOfflineMessages();
		}

		private void OnPersonaState(SteamFriends.PersonaStateCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			if (callback.FriendID == SteamClient.SteamID) {
				Events.OnPersonaState(this, callback);
				Statistics?.OnPersonaState(callback).Forget();
			} else if ((callback.FriendID == LibraryLockedBySteamID) && (callback.GameID == 0)) {
				LibraryLockedBySteamID = 0;
				CheckOccupationStatus();
			}
		}

		private void OnPICSProductInfo(SteamApps.PICSProductInfoCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
			}
		}

		private void OnPlayingSessionState(ArchiHandler.PlayingSessionStateCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			if (callback.PlayingBlocked == PlayingBlocked) {
				return; // No status update, we're not interested
			}

			PlayingBlocked = callback.PlayingBlocked;
			CheckOccupationStatus();
		}

		private void OnPurchaseResponse(ArchiHandler.PurchaseResponseCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
			}
		}

		private void OnSharedLibraryLockStatus(ArchiHandler.SharedLibraryLockStatusCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
				return;
			}

			// Ignore no status updates
			if (LibraryLockedBySteamID == 0) {
				if ((callback.LibraryLockedBySteamID == 0) || (callback.LibraryLockedBySteamID == SteamClient.SteamID)) {
					return;
				}

				LibraryLockedBySteamID = callback.LibraryLockedBySteamID;
			} else {
				if ((callback.LibraryLockedBySteamID != 0) && (callback.LibraryLockedBySteamID != SteamClient.SteamID)) {
					return;
				}

				if (SteamFriends.GetFriendGamePlayed(LibraryLockedBySteamID) != 0) {
					return;
				}

				LibraryLockedBySteamID = 0;
			}

			CheckOccupationStatus();
		}

		private void OnWebAPIUserNonce(SteamUser.WebAPIUserNonceCallback callback) {
			if (callback == null) {
				ArchiLogger.LogNullError(nameof(callback));
			}
		}

		private void ResetGamesPlayed() {
			if (!IsPlayingPossible || (FamilySharingInactivityTimer != null)) {
				return;
			}

			ArchiHandler.PlayGames(BotConfig.GamesPlayedWhileIdle, BotConfig.CustomGamePlayedWhileIdle);
		}

		private async Task<string> Response2FA(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!HasMobileAuthenticator) {
				return Environment.NewLine + Strings.BotNoASFAuthenticator;
			}

			string token = await BotDatabase.MobileAuthenticator.GenerateToken().ConfigureAwait(false);
			return Environment.NewLine + (!string.IsNullOrEmpty(token) ? string.Format(Strings.BotAuthenticatorToken, token) : Strings.WarningFailed);
		}

		private static async Task<string> Response2FA(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.Response2FA(steamID));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private async Task<string> Response2FAConfirm(ulong steamID, bool confirm) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			if (!HasMobileAuthenticator) {
				return Environment.NewLine + Strings.BotNoASFAuthenticator;
			}

			if (await AcceptConfirmations(confirm).ConfigureAwait(false)) {
				return Environment.NewLine + Strings.Success;
			}

			return Environment.NewLine + Strings.WarningFailed;
		}

		private static async Task<string> Response2FAConfirm(ulong steamID, string botNames, bool confirm) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.Response2FAConfirm(steamID, confirm));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private async Task<string> ResponseAddLicense(ulong steamID, ICollection<uint> gameIDs) {
			if ((steamID == 0) || (gameIDs == null) || (gameIDs.Count == 0)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(gameIDs) + " || " + nameof(gameIDs.Count));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			StringBuilder response = new StringBuilder();
			foreach (uint gameID in gameIDs) {
				SteamApps.FreeLicenseCallback callback = await SteamApps.RequestFreeLicense(gameID);
				if (callback == null) {
					response.Append(Environment.NewLine + string.Format(Strings.BotAddLicenseResponse, BotName, gameID, EResult.Timeout));
					break;
				}

				if (callback.GrantedApps.Count > 0) {
					response.Append(Environment.NewLine + string.Format(Strings.BotAddLicenseResponseWithItems, BotName, gameID, callback.Result, string.Join(", ", callback.GrantedApps)));
				} else if (callback.GrantedPackages.Count > 0) {
					response.Append(Environment.NewLine + string.Format(Strings.BotAddLicenseResponseWithItems, BotName, gameID, callback.Result, string.Join(", ", callback.GrantedPackages)));
				} else if (await ArchiWebHandler.AddFreeLicense(gameID).ConfigureAwait(false)) {
					response.Append(Environment.NewLine + string.Format(Strings.BotAddLicenseResponseWithItems, BotName, gameID, EResult.OK, gameID));
				} else {
					response.Append(Environment.NewLine + string.Format(Strings.BotAddLicenseResponse, BotName, gameID, EResult.AccessDenied));
				}
			}

			return response.Length > 0 ? response.ToString() : null;
		}

		private async Task<string> ResponseAddLicense(ulong steamID, string games) {
			if ((steamID == 0) || string.IsNullOrEmpty(games)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(games));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			string[] gameIDs = games.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

			HashSet<uint> gamesToRedeem = new HashSet<uint>();
			foreach (string game in gameIDs.Where(game => !string.IsNullOrEmpty(game))) {
				uint gameID;
				if (!uint.TryParse(game, out gameID)) {
					return Environment.NewLine + string.Format(Strings.ErrorParsingObject, nameof(gameID));
				}

				gamesToRedeem.Add(gameID);
			}

			if (gamesToRedeem.Count == 0) {
				return Environment.NewLine + string.Format(Strings.ErrorIsEmpty, nameof(gamesToRedeem));
			}

			return await ResponseAddLicense(steamID, gamesToRedeem).ConfigureAwait(false);
		}

		private static async Task<string> ResponseAddLicense(ulong steamID, string botNames, string games) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames) || string.IsNullOrEmpty(games)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames) + " || " + nameof(games));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.ResponseAddLicense(steamID, games));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private static string ResponseAPI(ulong steamID) {
			if (steamID != 0) {
				return IsOwner(steamID) ? Environment.NewLine + GetAPIStatus() : null;
			}

			ASF.ArchiLogger.LogNullError(nameof(steamID));
			return null;
		}

		private static string ResponseExit(ulong steamID) {
			if (steamID == 0) {
				ASF.ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsOwner(steamID)) {
				return null;
			}

			// Schedule the task after some time so user can receive response
			Task.Run(async () => {
				await Task.Delay(1000).ConfigureAwait(false);
				await Program.Exit().ConfigureAwait(false);
			}).Forget();

			return Environment.NewLine + Strings.Done;
		}

		private async Task<string> ResponseFarm(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			await CardsFarmer.StopFarming().ConfigureAwait(false);
			CardsFarmer.StartFarming().Forget();
			return Environment.NewLine + Strings.Done;
		}

		private static async Task<string> ResponseFarm(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.ResponseFarm(steamID));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private string ResponseHelp(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			return Environment.NewLine + "https://github.com/" + SharedInfo.GithubRepo + "/wiki/Commands";
		}

		private async Task<string> ResponseLoot(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			if (!LootingAllowed) {
				return Environment.NewLine + Strings.BotLootingTemporarilyDisabled;
			}

			if (BotConfig.SteamMasterID == 0) {
				return Environment.NewLine + Strings.BotLootingMasterNotDefined;
			}

			if (BotConfig.SteamMasterID == SteamClient.SteamID) {
				return Environment.NewLine + Strings.BotLootingYourself;
			}

			if (BotConfig.LootableTypes.Count == 0) {
				return Environment.NewLine + Strings.BotLootingNoLootableTypes;
			}

			await Trading.LimitInventoryRequestsAsync().ConfigureAwait(false);

			HashSet<Steam.Item> inventory = await ArchiWebHandler.GetMySteamInventory(true, BotConfig.LootableTypes).ConfigureAwait(false);
			if ((inventory == null) || (inventory.Count == 0)) {
				return Environment.NewLine + string.Format(Strings.ErrorIsEmpty, nameof(inventory));
			}

			if (!await ArchiWebHandler.SendTradeOffer(inventory, BotConfig.SteamMasterID, BotConfig.SteamTradeToken).ConfigureAwait(false)) {
				return Environment.NewLine + Strings.BotLootingFailed;
			}

			await Task.Delay(3000).ConfigureAwait(false); // Sometimes we can be too fast for Steam servers to generate confirmations, wait a short moment
			await AcceptConfirmations(true, Steam.ConfirmationDetails.EType.Trade, BotConfig.SteamMasterID).ConfigureAwait(false);
			return Environment.NewLine + Strings.BotLootingSuccess;
		}

		private static async Task<string> ResponseLoot(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.ResponseLoot(steamID));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

        private static async Task<string> ResponseTransfer(ulong steamID, string mode, string botNameFrom, string botNameTo) {
            //standard procedure adapted from loot
            if ((steamID == 0) || string.IsNullOrEmpty(botNameFrom) || string.IsNullOrEmpty(botNameTo) || string.IsNullOrEmpty(mode)) {
                //ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(mode) + " || " + nameof(botNameFrom) + " || " + nameof(botNameTo));
                return null;
            }

            Bot botFrom, botTo;
            if (!Bots.TryGetValue(botNameFrom, out botFrom) || !Bots.TryGetValue(botNameTo, out botTo)) {
                if (IsOwner(steamID)) {
                    return "Couldn't find one of the bots named " + botNameFrom + " or " + botNameTo + "!";
                }

                return null;
            }

            if (steamID == 0) {
                botFrom.ArchiLogger.LogNullError(nameof(steamID));
                return null;
            }

            if (!botFrom.IsMaster(steamID)) {
                return null;
            }

            if (!botFrom.IsConnectedAndLoggedOn) {
                return botNameFrom + " is not connected!";
            }

            if (botTo.SteamClient.SteamID == botFrom.SteamClient.SteamID) {
                return "You can't transfere to yourself!";
            }

            await Trading.LimitInventoryRequestsAsync().ConfigureAwait(false);
            HashSet<Steam.Item.EType> tmp = new HashSet<Steam.Item.EType>();
            tmp.Add(Steam.Item.EType.Unknown);
            tmp.Add(Steam.Item.EType.BoosterPack);
            tmp.Add(Steam.Item.EType.Emoticon);
            tmp.Add(Steam.Item.EType.FoilTradingCard);
            tmp.Add(Steam.Item.EType.ProfileBackground);
            tmp.Add(Steam.Item.EType.SteamGems);
            tmp.Add(Steam.Item.EType.TradingCard);
            HashSet<Steam.Item> inventory = await botFrom.ArchiWebHandler.GetMySteamInventory(true, tmp ).ConfigureAwait(false);
            HashSet<Steam.Item> inventoryFull = new HashSet<Steam.Item>(inventory);
            HashSet<Steam.Item> inventoryToSend = new HashSet<Steam.Item>();


            if ((inventory == null) || (inventory.Count == 0)) {
                return "Nothing to send, inventory seems empty!";
            }

            //first fill the amount of cards for badges we have ready to craft. (Count the amount of different cards).
            if (botFrom.GamesReady.Count > 0) {
                //just keep the normal cars for games we don't know the amount of cards.
                inventory.RemoveWhere(item => !botFrom.GamesReady.Contains(item.RealAppID));
                inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.TradingCard));

                List<ulong> added = new List<ulong>();
                foreach (Steam.Item item in inventory) {
                    if (added.Contains(item.ClassID)) continue; //card already counted

                    //count to game, the card is from
                    uint max;
                    if (botFrom.GamesMixed.TryGetValue(item.RealAppID, out max)) {
                        botFrom.GamesMixed[item.RealAppID] = max + 1;
                    } else {
                        botFrom.GamesMixed[item.RealAppID] = 1;
                    }

                    added.Add(item.ClassID);
                }
                botFrom.GamesReady.Clear(); // we have them in Games Mixed now.
                added.Clear();
            }
            // now we have a list of alle badges we have cards for with amount of 
            // cards needed for badge. (the more or the less)
            inventory = inventoryFull; //reset to full inventory.

            bool fullsets = false, sets = false, rest = false;
            //probably would be better, do different commands, 
            //instead of parameter, but would be much duplicated code.
            switch (mode.ToUpper()) {
                case "ALL":
                    //just send all normal cards.
                    inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.TradingCard));
                    break;
                case "FOIL":
                    //just send all foils.
                    inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.FoilTradingCard));
                    break;
                case "BOOSTER":
                    //just send all foils.
                    inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.BoosterPack));
                    break;
                case "EMOTICON":
                    //just send all emoticons.
                    inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.Emoticon));
                    break;
                case "BACKGROUND":
                    //just send all backgrounds.
                    inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.ProfileBackground));
                    break;
                case "BUTCARD":
                    //just send everything, BUT normal cards.
                    inventory.RemoveWhere(item => (item.Type == Steam.Item.EType.TradingCard));
                    break;
                case "EVERYTHING":
                    //just send everything.
                    break;
                case "FULLSET":
                    //just send all full sets (completed sets, ready to craft).
                    fullsets = true;
                    break;
                case "SET":
                    //just send all unfinished sets (enough cards, but to much dupes).
                    sets = true;
                    break;
                case "REST":
                    //just send all cards, that are defenitly not needed for sets.
                    rest = true;
                    break;
                case "UNKNOWN":
                    //just send normal cards, we do not have information about.
                    inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.TradingCard));
                    inventory.RemoveWhere(item => (botFrom.GamesMixed.ContainsKey(item.RealAppID)));
                    break;
                default:
                    return "Unknown mode " + mode + "!";

            }
            if (fullsets || sets || rest) {// only works for normal cards.
                inventory.RemoveWhere(item => (item.Type != Steam.Item.EType.TradingCard));
                //remove all we have no info about.
                inventory.RemoveWhere(item => (!botFrom.GamesMixed.ContainsKey(item.RealAppID)));
                //first create a map: for each game, count the amount for each classid
                // we use int for the amount, so we can use sum<>.
                Dictionary<uint, Dictionary<ulong, int>> amountMap = new Dictionary<uint, Dictionary<ulong, int>>();
                foreach (Steam.Item item in inventory) {
                    Dictionary<ulong, int> game;
                    if (amountMap.TryGetValue(item.RealAppID, out game)) {
                        int amount;
                        if (game.TryGetValue(item.ClassID, out amount)) {
                            game[item.ClassID] = amount + checked((int) item.Amount);
                        } else {
                            game[item.ClassID] = checked((int) item.Amount);
                        }
                        amountMap[item.RealAppID] = game; //needed?
                    } else {
                        game = new Dictionary<ulong, int>();
                        game[item.ClassID] = checked((int) item.Amount);
                        amountMap[item.RealAppID] = game;
                    }
                }
                //So for each game, we now have a list with classIds and amount.

                //gather a list of games, that will not be send at all.
                List<uint> removeGames = new List<uint>();
                //then we will adjust the amount in amountMap to what we want to send (0 or removed
                // means card will not be send)

                foreach (uint key in amountMap.Keys) {
                    Dictionary<ulong, int> cards;
                    amountMap.TryGetValue(key, out cards); //check not needed?

                    uint max;
                    //ignore game, we have no information about
                    if (!botFrom.GamesMixed.TryGetValue(key, out max)) continue;

                    int amountCards = cards.Values.Sum();
                    int amountMixedSets = (int) Math.Floor((double) amountCards / (double) max);



                    int amountSets = cards.Values.Min();
                    if (cards.Count != max) {
                        // We have no full set.
                        amountSets = 0;
                    }
                    amountMixedSets = amountMixedSets - amountSets;

                    if (rest) {
                        if (amountSets == 0 && amountMixedSets == 0) {
                            // only too few cards, send all
                            continue;
                        }
                        if ((amountMixedSets > 0) || ((amountCards - amountSets * max) == 0)) {
                            // enough to make more sets or no cards outside of sets, send nothing.
                            removeGames.Add(key);
                            continue;
                        }

                        if (amountSets > 0 && amountMixedSets == 0) {
                            // too less for new set, only send the cards not in a set.
                            foreach (ulong classid in cards.Keys.ToList<ulong>()) {
                                int am;
                                if (cards.TryGetValue(classid, out am)) {
                                    cards[classid] = am - amountSets;
                                }
                            }
                            continue;
                        }
                    }

                    if (fullsets) {
                        if (amountSets == 0) {
                            // no full set, send nothing
                            removeGames.Add(key);
                            continue;
                        }
                        // send as much of each, as we have sets.
                        foreach (ulong classid in cards.Keys.ToList<ulong>()) {
                            cards[classid] = amountSets;
                        }
                        continue;
                    }

                    if (sets) {
                        if (amountMixedSets == 0) {
                            // no unfinished set, send nothing
                            removeGames.Add(key);
                            continue;
                        }
                        // send everything, besides whats in a set.
                        foreach (ulong classid in cards.Keys.ToList<ulong>()) {
                            int am;
                            if (cards.TryGetValue(classid, out am)) {
                                cards[classid] = am - amountSets;
                            }
                        }
                        continue;
                    }
                }

                foreach (uint key in removeGames)
                    amountMap.Remove(key);
                //now we have a list of all classids with the amount we want to send (for each game).
                // put these in our new inventory and ignore the rest.
                foreach (Steam.Item item in inventory) {
                    Dictionary<ulong, int> cards;
                    if (!amountMap.TryGetValue(item.RealAppID, out cards)) continue;
                    int toSent;
                    if (!cards.TryGetValue(item.ClassID, out toSent)) continue;
                    if (toSent <= 0) continue;

                    inventoryToSend.Add(item);
                    cards[item.ClassID] = toSent - 1;
                }
            } else {
                //we do not need to filter after sets, fullsets or rest.
                inventoryToSend = inventory;
            }
            if (inventoryToSend.Count == 0) {
                return "No items to be send for " + mode + "!";
            }

            if (!await botFrom.ArchiWebHandler.SendTradeOffer(inventoryToSend, botTo.SteamClient.SteamID, null).ConfigureAwait(false)) {
                return "Trade offer failed due to error!";
            }

            await Task.Delay(1000).ConfigureAwait(false); // Sometimes we can be too fast for Steam servers to generate confirmations, wait a short moment
            await botFrom.AcceptConfirmations(true, Steam.ConfirmationDetails.EType.Trade, botTo.SteamClient.SteamID).ConfigureAwait(false);
            return "Trade offer sent successfully!";
        }

		private string ResponseLootSwitch(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			LootingAllowed = !LootingAllowed;
			return Environment.NewLine + (LootingAllowed ? Strings.BotLootingNowEnabled : Strings.BotLootingNowDisabled);
		}

		private static async Task<string> ResponseLootSwitch(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => Task.Run(() => bot.ResponseLootSwitch(steamID)));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private async Task<string> ResponseOwns(ulong steamID, string query) {
			if ((steamID == 0) || string.IsNullOrEmpty(query)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(query));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			Dictionary<uint, string> ownedGames;
			if (await ArchiWebHandler.HasValidApiKey().ConfigureAwait(false)) {
				ownedGames = await ArchiWebHandler.GetOwnedGames(SteamClient.SteamID).ConfigureAwait(false);
			} else {
				ownedGames = await ArchiWebHandler.GetMyOwnedGames().ConfigureAwait(false);
			}

			if ((ownedGames == null) || (ownedGames.Count == 0)) {
				return Environment.NewLine + string.Format(Strings.ErrorIsEmpty, nameof(ownedGames));
			}

			StringBuilder response = new StringBuilder();

			string[] games = query.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
			foreach (string game in games.Where(game => !string.IsNullOrEmpty(game))) {
				// Check if this is appID
				uint appID;
				if (uint.TryParse(game, out appID)) {
					string ownedName;
					if (ownedGames.TryGetValue(appID, out ownedName)) {
						response.Append(Environment.NewLine + string.Format(Strings.BotOwnedAlready, BotName, appID, ownedName));
					} else {
						response.Append(Environment.NewLine + string.Format(Strings.BotNotOwnedYet, BotName, appID));
					}

					continue;
				}

				// This is a string, so check our entire library
				foreach (KeyValuePair<uint, string> ownedGame in ownedGames.Where(ownedGame => ownedGame.Value.IndexOf(game, StringComparison.OrdinalIgnoreCase) >= 0)) {
					response.Append(Environment.NewLine + string.Format(Strings.BotOwnedAlready, BotName, ownedGame.Key, ownedGame.Value));
				}
			}

			return response.Length > 0 ? response.ToString() : Environment.NewLine + string.Format(Strings.BotNotOwnedYet, BotName, query);
		}

		private static async Task<string> ResponseOwns(ulong steamID, string botNames, string query) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames) || string.IsNullOrEmpty(query)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames) + " || " + nameof(query));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.ResponseOwns(steamID, query));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private string ResponsePassword(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (string.IsNullOrEmpty(BotConfig.SteamPassword)) {
				return Environment.NewLine + string.Format(Strings.ErrorIsEmpty, nameof(BotConfig.SteamPassword));
			}

			return Environment.NewLine + string.Format(Strings.BotEncryptedPassword, CryptoHelper.ECryptoMethod.AES, CryptoHelper.Encrypt(CryptoHelper.ECryptoMethod.AES, BotConfig.SteamPassword)) + Environment.NewLine + string.Format(Strings.BotEncryptedPassword, CryptoHelper.ECryptoMethod.ProtectedDataForCurrentUser, CryptoHelper.Encrypt(CryptoHelper.ECryptoMethod.ProtectedDataForCurrentUser, BotConfig.SteamPassword));
		}

		private static async Task<string> ResponsePassword(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => Task.Run(() => bot.ResponsePassword(steamID)));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private async Task<string> ResponsePause(ulong steamID, bool sticky) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID) && !SteamFamilySharingIDs.Contains(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			if (CardsFarmer.Paused) {
				return Environment.NewLine + Strings.BotAutomaticIdlingPausedAlready;
			}

			await CardsFarmer.Pause(sticky).ConfigureAwait(false);

			if (!SteamFamilySharingIDs.Contains(steamID)) {
				return Environment.NewLine + Strings.BotAutomaticIdlingNowPaused;
			}

			StartFamilySharingInactivityTimer();
			return Environment.NewLine + string.Format(Strings.BotAutomaticIdlingPausedWithCountdown, FamilySharingInactivityMinutes);
		}

		private static async Task<string> ResponsePause(ulong steamID, string botNames, bool sticky) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => Task.Run(() => bot.ResponsePause(steamID, sticky)));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private async Task<string> ResponsePlay(ulong steamID, HashSet<uint> gameIDs) {
			if ((steamID == 0) || (gameIDs == null) || (gameIDs.Count == 0)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(gameIDs) + " || " + nameof(gameIDs.Count));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			if (!CardsFarmer.Paused) {
				await CardsFarmer.Pause(false).ConfigureAwait(false);
			}

			ArchiHandler.PlayGames(gameIDs);
			return Environment.NewLine + Strings.Done;
		}

		private async Task<string> ResponsePlay(ulong steamID, string games) {
			if ((steamID == 0) || string.IsNullOrEmpty(games)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(games));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			string[] gameIDs = games.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

			HashSet<uint> gamesToPlay = new HashSet<uint>();
			foreach (string game in gameIDs.Where(game => !string.IsNullOrEmpty(game))) {
				uint gameID;
				if (!uint.TryParse(game, out gameID)) {
					return Environment.NewLine + string.Format(Strings.ErrorParsingObject, nameof(gameID));
				}

				gamesToPlay.Add(gameID);

				if (gamesToPlay.Count >= ArchiHandler.MaxGamesPlayedConcurrently) {
					break;
				}
			}

			if (gamesToPlay.Count == 0) {
				return Environment.NewLine + string.Format(Strings.ErrorIsEmpty, gamesToPlay);
			}

			return await ResponsePlay(steamID, gamesToPlay).ConfigureAwait(false);
		}

		private static async Task<string> ResponsePlay(ulong steamID, string botNames, string games) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames) || string.IsNullOrEmpty(games)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames) + " || " + nameof(games));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.ResponsePlay(steamID, games));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		[SuppressMessage("ReSharper", "FunctionComplexityOverflow")]
		private async Task<string> ResponseRedeem(ulong steamID, string message, ERedeemFlags redeemFlags = ERedeemFlags.None) {
			if ((steamID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(message));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			bool forward = !redeemFlags.HasFlag(ERedeemFlags.SkipForwarding) && (redeemFlags.HasFlag(ERedeemFlags.ForceForwarding) || BotConfig.RedeemingPreferences.HasFlag(BotConfig.ERedeemingPreferences.Forwarding));
			bool distribute = !redeemFlags.HasFlag(ERedeemFlags.SkipDistribution) && (redeemFlags.HasFlag(ERedeemFlags.ForceDistribution) || BotConfig.RedeemingPreferences.HasFlag(BotConfig.ERedeemingPreferences.Distributing));
			message = message.Replace(",", Environment.NewLine);

			HashSet<string> unusedKeys = new HashSet<string>();
			StringBuilder response = new StringBuilder();

			using (StringReader reader = new StringReader(message)) {
				using (IEnumerator<Bot> enumerator = Bots.OrderBy(bot => bot.Key).Select(bot => bot.Value).GetEnumerator()) {
					string key = reader.ReadLine();
					Bot currentBot = this;
					while (!string.IsNullOrEmpty(key) && (currentBot != null)) {
						if (redeemFlags.HasFlag(ERedeemFlags.Validate) && !IsValidCdKey(key)) {
							key = reader.ReadLine(); // Next key
							continue; // Keep current bot
						}

						unusedKeys.Add(key);

						if ((redeemFlags.HasFlag(ERedeemFlags.SkipInitial) && (currentBot == this)) || !currentBot.IsConnectedAndLoggedOn) {
							currentBot = null; // Either bot will be changed, or loop aborted
						} else {
							ArchiHandler.PurchaseResponseCallback result = await currentBot.ArchiHandler.RedeemKey(key).ConfigureAwait(false);
							if (result == null) {
								response.Append(Environment.NewLine + "<" + currentBot.BotName + "> Key: " + key + " | Status: " + ArchiHandler.PurchaseResponseCallback.EPurchaseResult.Timeout);
								currentBot = null; // Either bot will be changed, or loop aborted
							} else {
								switch (result.PurchaseResult) {
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.DuplicatedKey:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.InvalidKey:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.SteamWalletCode:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.Timeout:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.Unknown:
										if (result.PurchaseResult == ArchiHandler.PurchaseResponseCallback.EPurchaseResult.SteamWalletCode) {
											// If it's a wallet code, try to redeem it, and forward the result
											// The result is final, there is no place for forwarding
											result.PurchaseResult = await currentBot.ArchiWebHandler.RedeemWalletKey(key).ConfigureAwait(false);
										}

										if ((result.Items != null) && (result.Items.Count > 0)) {
											response.Append(Environment.NewLine + string.Format(Strings.BotRedeemResponseWithItems, currentBot.BotName, key, result.PurchaseResult, string.Join("", result.Items)));
										} else {
											response.Append(Environment.NewLine + string.Format(Strings.BotRedeemResponse, currentBot.BotName, key, result.PurchaseResult));
										}

										if (result.PurchaseResult != ArchiHandler.PurchaseResponseCallback.EPurchaseResult.Timeout) {
											unusedKeys.Remove(key);
										}

										key = reader.ReadLine(); // Next key

										if (result.PurchaseResult == ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK) {
											break; // Next bot (if needed)
										}

										continue; // Keep current bot
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.AlreadyOwned:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.BaseGameRequired:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OnCooldown:
									case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.RegionLocked:
										if ((result.Items != null) && (result.Items.Count > 0)) {
											response.Append(Environment.NewLine + string.Format(Strings.BotRedeemResponseWithItems, currentBot.BotName, key, result.PurchaseResult, string.Join("", result.Items)));
										} else {
											response.Append(Environment.NewLine + string.Format(Strings.BotRedeemResponse, currentBot.BotName, key, result.PurchaseResult));
										}

										if (!forward) {
											key = reader.ReadLine(); // Next key
											break; // Next bot (if needed)
										}

										if (distribute) {
											break; // Next bot, without changing key
										}

										Dictionary<uint, string> items = result.Items ?? new Dictionary<uint, string>();

										Bot previousBot = currentBot;
										bool alreadyHandled = false;
										foreach (Bot bot in Bots.Where(bot => (bot.Value != previousBot) && (!redeemFlags.HasFlag(ERedeemFlags.SkipInitial) || (bot.Value != this)) && bot.Value.IsConnectedAndLoggedOn && ((items.Count == 0) || items.Keys.Any(packageID => !bot.Value.OwnedPackageIDs.Contains(packageID)))).OrderBy(bot => bot.Key).Select(bot => bot.Value)) {
											ArchiHandler.PurchaseResponseCallback otherResult = await bot.ArchiHandler.RedeemKey(key).ConfigureAwait(false);
											if (otherResult == null) {
												response.Append(Environment.NewLine + string.Format(Strings.BotRedeemResponse, bot.BotName, key, EResult.Timeout));
												continue;
											}

											switch (otherResult.PurchaseResult) {
												case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.DuplicatedKey:
												case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.InvalidKey:
												case ArchiHandler.PurchaseResponseCallback.EPurchaseResult.OK:
													alreadyHandled = true; // This key is already handled, as we either redeemed it or we're sure it's dupe/invalid
													unusedKeys.Remove(key);
													break;
											}

											if ((otherResult.Items != null) && (otherResult.Items.Count > 0)) {
												response.Append(Environment.NewLine + string.Format(Strings.BotRedeemResponseWithItems, bot.BotName, key, otherResult.PurchaseResult, string.Join("", otherResult.Items)));
											} else {
												response.Append(Environment.NewLine + string.Format(Strings.BotRedeemResponse, bot.BotName, key, otherResult.PurchaseResult));
											}

											if (alreadyHandled) {
												break;
											}

											if (otherResult.Items == null) {
												continue;
											}

											foreach (KeyValuePair<uint, string> item in otherResult.Items) {
												items[item.Key] = item.Value;
											}
										}

										key = reader.ReadLine(); // Next key
										break; // Next bot (if needed)
								}
							}
						}

						if (!distribute && !redeemFlags.HasFlag(ERedeemFlags.SkipInitial)) {
							continue;
						}

						do {
							currentBot = enumerator.MoveNext() ? enumerator.Current : null;
						} while ((currentBot == this) || ((currentBot != null) && !currentBot.IsConnectedAndLoggedOn));
					}
				}
			}

			if (unusedKeys.Count > 0) {
				response.Append(Environment.NewLine + string.Format(Strings.UnusedKeys, string.Join(", ", unusedKeys)));
			}

			return response.Length > 0 ? response.ToString() : null;
		}

		private static async Task<string> ResponseRedeem(ulong steamID, string botNames, string message, ERedeemFlags redeemFlags = ERedeemFlags.None) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames) || string.IsNullOrEmpty(message)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames) + " || " + nameof(message));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => bot.ResponseRedeem(steamID, message, redeemFlags));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private static string ResponseRejoinChat(ulong steamID) {
			if (steamID == 0) {
				ASF.ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsOwner(steamID)) {
				return null;
			}

			foreach (Bot bot in Bots.Values) {
				bot.JoinMasterChat();
			}

			return Environment.NewLine + Strings.Done;
		}

		private static string ResponseRestart(ulong steamID) {
			if (steamID == 0) {
				ASF.ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsOwner(steamID)) {
				return null;
			}

			// Schedule the task after some time so user can receive response
			Task.Run(async () => {
				await Task.Delay(1000).ConfigureAwait(false);
				await Program.Restart().ConfigureAwait(false);
			}).Forget();

			return Environment.NewLine + Strings.Done;
		}

		private string ResponseResume(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID) && !SteamFamilySharingIDs.Contains(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + Strings.BotNotConnected;
			}

			if (!CardsFarmer.Paused) {
				return Environment.NewLine + Strings.BotAutomaticIdlingResumedAlready;
			}

			StopFamilySharingInactivityTimer();
			CardsFarmer.Resume(true);
			return Environment.NewLine + Strings.BotAutomaticIdlingNowResumed;
		}

		private static async Task<string> ResponseResume(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => Task.Run(() => bot.ResponseResume(steamID)));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private string ResponseStart(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (KeepRunning) {
				return Environment.NewLine + Strings.BotAlreadyRunning;
			}

			SkipFirstShutdown = true;
			Start().Forget();
			return Environment.NewLine + Strings.Done;
		}

		private static async Task<string> ResponseStart(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => Task.Run(() => bot.ResponseStart(steamID)));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private string ResponseStatus(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!IsConnectedAndLoggedOn) {
				return Environment.NewLine + string.Format(KeepRunning ? Strings.BotStatusNotConnected : Strings.BotStatusNotRunning, BotName);
			}

			if (PlayingBlocked) {
				return Environment.NewLine + string.Format(Strings.BotStatusPlayingNotAvailable, BotName);
			}

			if (CardsFarmer.Paused) {
				return Environment.NewLine + string.Format(Strings.BotStatusPaused, BotName);
			}

			if (IsLimitedUser) {
				return Environment.NewLine + string.Format(Strings.BotStatusLimited, BotName);
			}

			if (CardsFarmer.CurrentGamesFarming.Count == 0) {
				return Environment.NewLine + string.Format(Strings.BotsStatusNotIdling, BotName);
			}

			if (CardsFarmer.CurrentGamesFarming.Count > 1) {
				return Environment.NewLine + string.Format(Strings.BotStatusIdlingList, BotName, string.Join(", ", CardsFarmer.CurrentGamesFarming.Select(game => game.AppID)), CardsFarmer.GamesToFarm.Count, CardsFarmer.GamesToFarm.Sum(game => game.CardsRemaining), CardsFarmer.TimeRemaining.ToHumanReadable());
			}

			CardsFarmer.Game soloGame = CardsFarmer.CurrentGamesFarming.First();
			return Environment.NewLine + string.Format(Strings.BotStatusIdling, BotName, soloGame.AppID, soloGame.GameName, soloGame.CardsRemaining, CardsFarmer.GamesToFarm.Count, CardsFarmer.GamesToFarm.Sum(game => game.CardsRemaining), CardsFarmer.TimeRemaining.ToHumanReadable());
		}

		private static async Task<string> ResponseStatus(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => Task.Run(() => bot.ResponseStatus(steamID)));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private string ResponseStop(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			if (!KeepRunning) {
				return Environment.NewLine + Strings.BotAlreadyStopped;
			}

			Stop();
			return Environment.NewLine + Strings.Done;
		}

		private static async Task<string> ResponseStop(ulong steamID, string botNames) {
			if ((steamID == 0) || string.IsNullOrEmpty(botNames)) {
				ASF.ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(botNames));
				return null;
			}

			HashSet<Bot> bots = GetBots(botNames);
			if ((bots == null) || (bots.Count == 0)) {
				return null;
			}

			ICollection<string> results;
			IEnumerable<Task<string>> tasks = bots.Select(bot => Task.Run(() => bot.ResponseStop(steamID)));

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					results = new List<string>(bots.Count);
					foreach (Task<string> task in tasks) {
						results.Add(await task.ConfigureAwait(false));
					}

					break;
				default:
					results = await Task.WhenAll(tasks).ConfigureAwait(false);
					break;
			}

			List<string> responses = new List<string>(results.Where(result => !string.IsNullOrEmpty(result)));
			return responses.Count > 0 ? string.Join("", responses) : null;
		}

		private string ResponseUnknown(ulong steamID) {
			if (steamID != 0) {
				return IsMaster(steamID) ? Environment.NewLine + Strings.UnknownCommand : null;
			}

			ArchiLogger.LogNullError(nameof(steamID));
			return null;
		}

		private static async Task<string> ResponseUpdate(ulong steamID) {
			if (steamID == 0) {
				ASF.ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsOwner(steamID)) {
				return null;
			}

			await ASF.CheckForUpdate(true).ConfigureAwait(false);
			return Environment.NewLine + Strings.Done;
		}

		private string ResponseVersion(ulong steamID) {
			if (steamID == 0) {
				ArchiLogger.LogNullError(nameof(steamID));
				return null;
			}

			if (!IsMaster(steamID)) {
				return null;
			}

			return Environment.NewLine + "ASF V" + SharedInfo.Version;
		}

		private void SendMessageToChannel(ulong steamID, string message) {
			if ((steamID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(message));
				return;
			}

			if (!IsConnectedAndLoggedOn) {
				return;
			}

			for (int i = 0; i < message.Length; i += MaxSteamMessageLength - 6) {
				string messagePart = (i > 0 ? "..." : "") + message.Substring(i, Math.Min(MaxSteamMessageLength - 6, message.Length - i)) + (MaxSteamMessageLength - 6 < message.Length - i ? "..." : "");
				SteamFriends.SendChatRoomMessage(steamID, EChatEntryType.ChatMsg, messagePart);
			}
		}

		private void SendMessageToUser(ulong steamID, string message) {
			if ((steamID == 0) || string.IsNullOrEmpty(message)) {
				ArchiLogger.LogNullError(nameof(steamID) + " || " + nameof(message));
				return;
			}

			if (!IsConnectedAndLoggedOn) {
				return;
			}

			for (int i = 0; i < message.Length; i += MaxSteamMessageLength - 6) {
				string messagePart = (i > 0 ? "..." : "") + message.Substring(i, Math.Min(MaxSteamMessageLength - 6, message.Length - i)) + (MaxSteamMessageLength - 6 < message.Length - i ? "..." : "");
				SteamFriends.SendChatMessage(steamID, EChatEntryType.ChatMsg, messagePart);
			}
		}

		private async Task Start() {
			if (!KeepRunning) {
				KeepRunning = true;
				Task.Factory.StartNew(HandleCallbacks, TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning).Forget();
				ArchiLogger.LogGenericInfo(Strings.Starting);
			}

			await Connect().ConfigureAwait(false);
		}

		private void StartFamilySharingInactivityTimer() {
			if (FamilySharingInactivityTimer != null) {
				return;
			}

			FamilySharingInactivityTimer = new Timer(
				e => CheckFamilySharingInactivity(),
				null,
				TimeSpan.FromMinutes(FamilySharingInactivityMinutes), // Delay
				Timeout.InfiniteTimeSpan // Period
			);
		}

		private void StopConnectionFailureTimer() {
			if (ConnectionFailureTimer == null) {
				return;
			}

			ConnectionFailureTimer.Dispose();
			ConnectionFailureTimer = null;
		}

		private void StopFamilySharingInactivityTimer() {
			if (FamilySharingInactivityTimer == null) {
				return;
			}

			FamilySharingInactivityTimer.Dispose();
			FamilySharingInactivityTimer = null;
		}

		[Flags]
		private enum ERedeemFlags : byte {
			None = 0,
			Validate = 1,
			ForceForwarding = 2,
			SkipForwarding = 4,
			ForceDistribution = 8,
			SkipDistribution = 16,
			SkipInitial = 32
		}
	}
}