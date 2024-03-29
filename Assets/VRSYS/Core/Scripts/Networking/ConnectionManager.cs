// VRSYS plugin of Virtual Reality and Visualization Group (Bauhaus-University Weimar)
//  _    ______  _______  _______
// | |  / / __ \/ ___/\ \/ / ___/
// | | / / /_/ /\__ \  \  /\__ \ 
// | |/ / _, _/___/ /  / /___/ / 
// |___/_/ |_|/____/  /_//____/  
//
//  __                            __                       __   __   __    ___ .  . ___
// |__)  /\  |  | |__|  /\  |  | /__`    |  | |\ | | \  / |__  |__) /__` |  |   /\   |  
// |__) /~~\ \__/ |  | /~~\ \__/ .__/    \__/ | \| |  \/  |___ |  \ .__/ |  |  /~~\  |  
//
//       ___               __                                                           
// |  | |__  |  |\/|  /\  |__)                                                          
// |/\| |___ |  |  | /~~\ |  \                                                                                                                                                                                     
//
// Copyright (c) 2023 Virtual Reality and Visualization Group
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:

// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.

// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
//-----------------------------------------------------------------
//   Authors:        Tony Zoeppig, Sebastian Muehlhaus
//   Date:           2023
//-----------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.Events;
using VRSYS.Core.Logging;
using VRSYS.Core.ScriptableObjects;
using Random = UnityEngine.Random;
using Unity.Netcode;

namespace VRSYS.Core.Networking
{
    public class ConnectionManager : MonoBehaviour
    {
        #region Member Variables
        
        // Singleton
        public static ConnectionManager Instance;

        [Header("Network User Spawn Info")]
        public NetworkUserSpawnInfo userSpawnInfo;

        [Header("Dedicated Server Properties")]
        public bool startDedicatedServer = false;
        public DedicatedServerSettings dedicatedServerSettings;

        [Header("Lobby Properties")] 
        public LobbySettings lobbySettings;

        [Header("Debugging")] 
        [SerializeField] private bool verbose = false;

        [Header("Events")] 
        public UnityEvent onAuthenticated = new UnityEvent();
        public UnityEvent onLobbyCreated = new UnityEvent();
        public UnityEvent onLobbyJoined = new UnityEvent();
        
        // Connection parameters
        [HideInInspector] public Lobby lobby;
        private string lobbyId;
        private bool isLobbyCreator = false;
        private RelayHostData hostData;
        private RelayJoinData joinData;

        // Connection state
        private ConnectionState connectionState = ConnectionState.Offline;
        [HideInInspector] public UnityEvent<ConnectionState> onConnectionStateChange = new UnityEvent<ConnectionState>();
        
        // Lobby List Update
        public float lobbyUpdateInterval = 5f;
        private bool updateLobby = false;
        [HideInInspector] public UnityEvent<List<LobbyData>> onLobbyListUpdated = new UnityEvent<List<LobbyData>>();

        private static string authenticatorGameObjectName = "UnityServicesAuthenticator";

        //timer to start after the lobby is created
        private System.Diagnostics.Stopwatch lobbyTimer;

        //timer duration
        private int timerLength = 5;






        #endregion

        #region MonoBehaviour Callbacks

        private void Awake()
        {
            if(Instance != null)
            {
                if(verbose)
                    ExtendedLogger.LogInfo(GetType().Name, "destroying previous connection manager");
                DestroyImmediate(Instance.gameObject);
                Instance = null;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private async void Start()
        {

            if (connectionState == ConnectionState.Offline)
            {
                connectionState = ConnectionState.Connecting;
                onConnectionStateChange.Invoke(connectionState);
                
                // Create unique profile to distinquish between different local users
                var options = new InitializationOptions();
                options.SetProfile("Player" + Random.Range(0, 1000));
                
                // Initialize Unity Services
                await UnityServices.InitializeAsync(options);
            
                // Setup event listeners
                SetupAuthEvents();
            
                // Unity Login
                await AuthSignInAnonymouslyAsync();

                connectionState = ConnectionState.Online;
                onConnectionStateChange.Invoke(connectionState);
                onAuthenticated.Invoke();
                
                // Dedicated server handling
                if (startDedicatedServer)
                {
                    StartDedicatedServer();
                    return;
                }

                updateLobby = true;
                InvokeRepeating(nameof(UpdateLobbyList), 0.5f, lobbyUpdateInterval);
                
                if(lobbySettings.autoStart)
                    AutoStart();
            }
        }

        private void OnDestroy()
        {
            // delete lobby when not used
            if (isLobbyCreator)
            {
                if(verbose)
                    ExtendedLogger.LogInfo(GetType().Name, "deleting lobby " + lobbyId);
                Lobbies.Instance.DeleteLobbyAsync(lobbyId);
            }
            AuthSignOut();
            Instance = null;
        }

        public async void KickPlayer(string playerId)
        {
            if (isLobbyCreator)
            {
                if(verbose)
                    ExtendedLogger.LogInfo(GetType().Name, "kicking player " + playerId);
                await Lobbies.Instance.RemovePlayerAsync(lobbyId, playerId);
            }
        }

        #endregion

        #region Unity Login

        private void SetupAuthEvents()
        {
            AuthenticationService.Instance.SignedIn += OnAuthSignedIn;
            AuthenticationService.Instance.SignInFailed += OnAuthSignInFailed;
            AuthenticationService.Instance.SignedOut += OnAuthSignedOut;
        }

        private void RemoveAuthEvents()
        {
            AuthenticationService.Instance.SignedIn -= OnAuthSignedIn;
            AuthenticationService.Instance.SignInFailed -= OnAuthSignInFailed;
            AuthenticationService.Instance.SignedOut -= OnAuthSignedOut;
        }

        private void OnAuthSignedIn()
        {
            if (verbose)
            {
                // Player ID
                ExtendedLogger.LogInfo(GetType().Name, $"PlayerID: {AuthenticationService.Instance.PlayerId}");

                // Access Token
                ExtendedLogger.LogInfo(GetType().Name, $"Access Token: {AuthenticationService.Instance.AccessToken}");
            }
        }

        private void OnAuthSignInFailed(RequestFailedException err)
        {
            Debug.LogError(err);
        }
        
        private void OnAuthSignedOut()
        {
            if (verbose)
                ExtendedLogger.LogInfo(GetType().Name, "player signed out.");
        }

        async Task AuthSignInAnonymouslyAsync()
        {
            try
            {
                var authGameObject = GameObject.Find(authenticatorGameObjectName);
                if (authGameObject == null)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();

                    if (verbose)
                        ExtendedLogger.LogInfo(GetType().Name, "sign in anonymously succeeded!");
                    
                    authGameObject = new GameObject(authenticatorGameObjectName);
                    DontDestroyOnLoad(authGameObject);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
        
        private void AuthSignOut()
        {
            try
            {
                var authGameObject = GameObject.Find(authenticatorGameObjectName);
                if (authGameObject != null)
                {
                    AuthenticationService.Instance.SignOut();
                    DestroyImmediate(authGameObject);
                    RemoveAuthEvents();
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        #endregion

        #region Lobby

        public async void CreateLobby()
        {
            if(verbose)
                ExtendedLogger.LogInfo(GetType().Name, "creating a new lobby...");
            
            // External connections
            int maxConnections = lobbySettings.maxUsers - 1;
            
            try
            {
                // Create RELAY object
                Allocation allocation = await Relay.Instance.CreateAllocationAsync(maxConnections);
                hostData = new RelayHostData()
                {
                    Key = allocation.Key,
                    Port = (ushort)allocation.RelayServer.Port,
                    AllocationID = allocation.AllocationId,
                    AllocationIDBytes = allocation.AllocationIdBytes,
                    ConnectionData = allocation.ConnectionData,
                    IPv4Address = allocation.RelayServer.IpV4
                };
                
                // Retrieve JoinCode
                hostData.JoinCode = await Relay.Instance.GetJoinCodeAsync(allocation.AllocationId);
                
                // handle lobby name
                if (lobbySettings.lobbyName.Equals(""))
                {
                    lobbySettings.lobbyName = "Lobby_" + Random.Range(1, 1000);
                }
                
                CreateLobbyOptions options = new CreateLobbyOptions();
                options.IsPrivate = lobbySettings.isPrivate;
                
                // Put the JoinCode in the lobby data, visible by every member
                options.Data = new Dictionary<string, DataObject>()
                {
                    {
                        "joinCode", new DataObject(
                            visibility: DataObject.VisibilityOptions.Member,
                            value: hostData.JoinCode)
                    },
                };

                // Create the lobby
                lobby = await Lobbies.Instance.CreateLobbyAsync(lobbySettings.lobbyName, lobbySettings.maxUsers, options);
                
                //start the timer
                lobbyTimer = System.Diagnostics.Stopwatch.StartNew();


                // Save Lobby ID for later users
                lobbyId = lobby.Id;

                if (verbose)
                    ExtendedLogger.LogInfo(GetType().Name, "created lobby: " + lobby.Name + ", ID: " + lobby.Id);

                // Heartbeat the lobby every 15 seconds
                StartCoroutine(HeartbeatLobbyCoroutine(lobby.Id, 15));

                //to trigger the initial sequence
                //StartCoroutine(LogElapsedTimeCoroutine());

                // Relay & Lobby are set

                // Set Transport data
                Unity.Netcode.NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(
                    hostData.IPv4Address,
                    hostData.Port,
                    hostData.AllocationIDBytes,
                    hostData.Key,
                    hostData.ConnectionData);
                
                // Start Host
                Unity.Netcode.NetworkManager.Singleton.StartHost();
                
                // Stop querying lobbies
                updateLobby = false;

                isLobbyCreator = true;
                connectionState = ConnectionState.JoinedLobby;
                onConnectionStateChange.Invoke(connectionState);
                onLobbyCreated.Invoke();
            }
            catch (LobbyServiceException e)
            {
                Debug.LogError(e);
                throw;
            }
        }

        /***Team Jaime***
         * 
         * A timer for the initial sequence to play.
         */
        private IEnumerator LogElapsedTimeCoroutine()
        {
            int timeLeft;

            // For now it is playing from the interactable cube. TODO: Use this variable.
            NetworkedAudioPlayer initialAudioPlayer = GameObject
                .Find("InteractableCube")
                .GetComponent<NetworkedAudioPlayer>();

            while (lobbyTimer.Elapsed.TotalSeconds < timerLength)
            {
                yield return new WaitForSeconds(1); // Wait for 1 second
                timeLeft = timerLength - (int)lobbyTimer.Elapsed.TotalSeconds;

                ExtendedLogger.LogInfo(GetType().Name, "First sequence will be played in: " + timeLeft.ToString());
            }
        }
 
        private IEnumerator HeartbeatLobbyCoroutine(string lobbyName, float waitTimeSeconds)
        {
            var delay = new WaitForSecondsRealtime(waitTimeSeconds);

            while (true)
            {
                Lobbies.Instance.SendHeartbeatPingAsync(lobbyName);
                
                if(verbose)
                    ExtendedLogger.LogInfo(GetType().Name, "Lobby Heartbeat");
                
                yield return delay;
            }
        }

        private async void UpdateLobbyList()
        {
            if(verbose)
                ExtendedLogger.LogInfo(GetType().Name, "starting lobby update");
            if (updateLobby)
            {
                try
                {
                    QueryLobbiesOptions options = new QueryLobbiesOptions();

                    // Filter for open lobbies only
                    options.Filters = new List<QueryFilter>()
                    {
                        new QueryFilter(
                            field: QueryFilter.FieldOptions.AvailableSlots,
                            op: QueryFilter.OpOptions.GT,
                            value: "0")
                    };

                    QueryResponse response = await Lobbies.Instance.QueryLobbiesAsync(options);

                    List<LobbyData> lobbyDatas = new List<LobbyData>();
                    
                    foreach (var lobby in response.Results)
                    {
                        LobbyData lobbyData = new LobbyData
                        {
                            LobbyId = lobby.Id,
                            LobbyName = lobby.Name,
                            CurrentUser = lobby.Players.Count,
                            MaxUser = lobby.MaxPlayers
                        };
                        
                        lobbyDatas.Add(lobbyData);
                    }
                    
                    if(verbose)
                        ExtendedLogger.LogInfo(GetType().Name, "lobby count" + lobbyDatas.Count);
                
                    onLobbyListUpdated.Invoke(lobbyDatas);
                }
                catch (LobbyServiceException e)
                {
                    Debug.LogError(e);
                }
            }
            
        }

        public async void JoinLobby(string lobbyId)
        {
            try
            {
                // Join selected lobby
                lobby = await Lobbies.Instance.JoinLobbyByIdAsync(lobbyId);
                lobbySettings.lobbyName = lobby.Name;
                this.lobbyId = lobbyId;

                if (verbose)
                {
                    /***Property of team Jaime***
                     * 
                     * TODO: Will need to add a server RPC here to update pad info.
                     *       The server needs to send each client what their audioPath needs to be.
                     *       Which will then be set as audioSource.clip.
                     */                    
                    string audioPath = GameObject.Find("MadPads").transform
                        .GetChild(0)
                        .GetChild(0)
                        .GetChild(0)
                        .GetChild(0).gameObject
                        .GetComponent<NetworkedAudioPlayer>()
                        .audioPath.Value.ToString();

                    ExtendedLogger.LogInfo(GetType().Name, "joined lobby: " + lobby.Id);
                    ExtendedLogger.LogInfo(GetType().Name, "sample " + audioPath);
                    ExtendedLogger.LogInfo(GetType().Name, "lobby players: " + lobby.Players.Capacity);
                }
                
                // Retrieve Relay code
                string joinCode = lobby.Data["joinCode"].Value;
                
                if(verbose)
                    ExtendedLogger.LogInfo(GetType().Name, "received JoinCode: " + joinCode);

                JoinAllocation allocation = await Relay.Instance.JoinAllocationAsync(joinCode);
                
                // Create join object
                joinData = new RelayJoinData
                {
                    Key = allocation.Key,
                    Port = (ushort)allocation.RelayServer.Port,
                    AllocationID = allocation.AllocationId,
                    AllocationIDBytes = allocation.AllocationIdBytes,
                    ConnectionData = allocation.ConnectionData,
                    HostConnectionData = allocation.HostConnectionData,
                    IPv4Address = allocation.RelayServer.IpV4
                };
                
                // Set Transport data
                Unity.Netcode.NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(
                    joinData.IPv4Address,
                    joinData.Port,
                    joinData.AllocationIDBytes,
                    joinData.Key,
                    joinData.ConnectionData,
                    joinData.HostConnectionData);
                
                // Start Client
                Unity.Netcode.NetworkManager.Singleton.StartClient();
                
                // Stop querying lobbies
                updateLobby = false;
                
                connectionState = ConnectionState.JoinedLobby;
                onConnectionStateChange.Invoke(connectionState);
                onLobbyJoined.Invoke();
            }
            catch (LobbyServiceException e)
            {
                // If no lobby could be found, create a new one
                if(verbose)
                    ExtendedLogger.LogError(GetType().Name, "could not join the lobby: " + e);
            }
        }

        public async void AutoStart()
        {
            if(verbose)
                ExtendedLogger.LogInfo(GetType().Name, "auto starting...");

            if (!string.IsNullOrEmpty(lobbySettings.lobbyName))
            {
                try
                {
                    QueryLobbiesOptions options = new QueryLobbiesOptions();
                
                    // Filter for open lobbies only
                    options.Filters = new List<QueryFilter>()
                    {
                        new QueryFilter(
                            field: QueryFilter.FieldOptions.AvailableSlots,
                            op: QueryFilter.OpOptions.GT,
                            value: "0")
                    };

                    QueryResponse result = await Lobbies.Instance.QueryLobbiesAsync(options);
                    Lobby lobby = result.Results.Find(l => l.Name == lobbySettings.lobbyName);

                    if (lobby != null)
                    {
                        JoinLobby(lobby.Id);
                    }
                    else
                    {
                        CreateLobby();
                    }
                }
                catch (LobbyServiceException e)
                {
                    Debug.LogError(e);
                }
            }
            else
            {
                CreateLobby();
            }
        }

        public async void StartDedicatedServer()
        {
            if(verbose)
                ExtendedLogger.LogInfo(GetType().Name, "creating a new dedicated server...");
            
            // Read dedicated server config file
            dedicatedServerSettings.ParseJsonConfigFile();
            
            // External connections
            int maxConnections = dedicatedServerSettings.jsonConfig.MaxConnections;

            try
            {
                // Create RELAY object
                Allocation allocation = await Relay.Instance.CreateAllocationAsync(maxConnections);
                hostData = new RelayHostData()
                {
                    Key = allocation.Key,
                    Port = (ushort)allocation.RelayServer.Port,
                    AllocationID = allocation.AllocationId,
                    AllocationIDBytes = allocation.AllocationIdBytes,
                    ConnectionData = allocation.ConnectionData,
                    IPv4Address = allocation.RelayServer.IpV4
                };
                
                // Retrieve JoinCode
                hostData.JoinCode = await Relay.Instance.GetJoinCodeAsync(allocation.AllocationId);
                
                // handle lobby name
                if (!(dedicatedServerSettings.jsonConfig.LobbyName.Length > 0))
                {
                    if (string.IsNullOrEmpty(lobbySettings.lobbyName))
                    {
                        lobbySettings.lobbyName = "Lobby_" + Random.Range(1, 1000);
                    }
                }
                else
                {
                    lobbySettings.lobbyName = dedicatedServerSettings.jsonConfig.LobbyName;
                }

                CreateLobbyOptions options = new CreateLobbyOptions();
                options.IsPrivate = lobbySettings.isPrivate;
                
                // Put the JoinCode in the lobby data, visible by every member
                options.Data = new Dictionary<string, DataObject>()
                {
                    {
                        "joinCode", new DataObject(
                            visibility: DataObject.VisibilityOptions.Member,
                            value: hostData.JoinCode)
                    },
                };

                // Create the lobby
                var lobby = await Lobbies.Instance.CreateLobbyAsync(lobbySettings.lobbyName, maxConnections, options);
                
                // Save Lobby ID for later users
                lobbyId = lobby.Id;

                if (verbose)
                    ExtendedLogger.LogInfo(GetType().Name, "created lobby: " + lobby.Name + ", ID: " + lobby.Id);

                // Heartbeat the lobby every 15 seconds
                StartCoroutine(HeartbeatLobbyCoroutine(lobby.Id, 15));
                
                // Relay & Lobby are set
                
                // Set Transport data
                Unity.Netcode.NetworkManager.Singleton.GetComponent<UnityTransport>().SetRelayServerData(
                    hostData.IPv4Address,
                    hostData.Port,
                    hostData.AllocationIDBytes,
                    hostData.Key,
                    hostData.ConnectionData);
                
                // Start Host
                Unity.Netcode.NetworkManager.Singleton.StartServer();
                
                // Stop querying lobbies
                updateLobby = false;

                connectionState = ConnectionState.JoinedLobby;
                onConnectionStateChange.Invoke(connectionState);
                onLobbyCreated.Invoke();
            }
            catch (LobbyServiceException e)
            {
                Debug.LogError(e);
                throw;
            }
        }

        #endregion

        #region Relay Data Structs

        /// <summary>
        /// RelayHostData represents the necessary information
        /// for a host to host a game on a Relay server
        /// </summary>
        public struct RelayHostData
        {
            public string JoinCode;
            public string IPv4Address;
            public ushort Port;
            public Guid AllocationID;
            public byte[] AllocationIDBytes;
            public byte[] ConnectionData;
            public byte[] Key;
        }

        /// <summary>
        /// RelayJoinData represents the necessary information
        /// to join a game on a Relay server
        /// </summary>
        public struct RelayJoinData
        {
            public string JoinCode;
            public string IPv4Address;
            public ushort Port;
            public Guid AllocationID;
            public byte[] AllocationIDBytes;
            public byte[] ConnectionData;
            public byte[] HostConnectionData;
            public byte[] Key;
        }

        #endregion

        #region Other Data Structs

        public struct LobbyData
        {
            public string LobbyId;
            public string LobbyName;
            public int CurrentUser;
            public int MaxUser;
        }

        #endregion
    }   
}