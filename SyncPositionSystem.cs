/*
MIT License

Copyright (c) 2021 James Frowen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mirage;
using Mirage.Logging;
using Mirage.Serialization;
using Godot;

namespace JamesFrowen.PositionSync
{
    public static class Benchmark
    {
        private static long[] frames;
        private static int index;
        private static bool isRecording;
        private static long start;

        public static event Action<long[]> RecordingFinished;

        public static bool IsRecording => isRecording;

        public static void StartRecording(int frameCount)
        {
            frames = new long[frameCount];
            isRecording = true;
            index = 0;
        }

        public static void StartFrame()
        {
            if (!isRecording) return;

            start = Stopwatch.GetTimestamp();
        }
        public static void EndFrame()
        {
            if (!isRecording) return;

            var end = Stopwatch.GetTimestamp();
            frames[index] = end - start;
            index++;
            if (index >= frames.Length)
            {
                RecordingFinished?.Invoke(frames);
                isRecording = false;
            }
        }
    }

    public class SyncPositionBehaviourCollection
    {
        private static readonly ILogger logger = LogFactory.GetLogger<SyncPositionBehaviourCollection>();

        private Dictionary<NetworkBehaviour.Id, SyncPositionBehaviour> _behaviours = new Dictionary<NetworkBehaviour.Id, SyncPositionBehaviour>();
        private readonly bool _includeComponentIndex;

        public SyncPositionBehaviourCollection(SyncSettings settings)
        {
            _includeComponentIndex = settings.IncludeComponentIndex;
        }

        public IReadOnlyDictionary<NetworkBehaviour.Id, SyncPositionBehaviour> Dictionary => _behaviours;


        public bool TryGetValue(NetworkBehaviour.Id id, out SyncPositionBehaviour value)
        {
           // UnityEngine.Debug.Assert(!_includeComponentIndex || id.ComponentIndex == 0, "ComponentIndex was not zero when _includeComponentIndex was disabled");
            return _behaviours.TryGetValue(id, out value);
        }

        public void AddBehaviour(SyncPositionBehaviour thing)
        {
            if (logger.LogEnabled()) logger.Log($"Added {thing.Identity.NetId}");
            var id = GetId(thing);
            _behaviours.Add(id, thing);
        }

        public void RemoveBehaviour(SyncPositionBehaviour thing)
        {
            if (logger.LogEnabled()) logger.Log($"Removed {thing.Identity.NetId}");
            var id = GetId(thing);
            _behaviours.Remove(id);
        }
        public void ClearBehaviours()
        {
            if (logger.LogEnabled()) logger.Log($"Cleared");
            _behaviours.Clear();
        }

        private NetworkBehaviour.Id GetId(SyncPositionBehaviour thing)
        {
            if (_includeComponentIndex)
                return new NetworkBehaviour.Id(thing.Identity.NetId, thing.ComponentIndex);
            else
                return new NetworkBehaviour.Id(thing.Identity.NetId, 0);
        }
    }

    [Serializable]
    public enum SyncMode
    {
        SendToAll = 1,
        SendToObservers_PlayerDirty = 2,
        SendToObservers_PlayerDirty_PackOnce = 5,
        SendToObservers_DirtyObservers = 3,
        SendToDirtyObservers_PackOnce = 4,
    }

    //[AddComponentMenu("Network/SyncPosition/SyncPositionSystem")]
    public partial class SyncPositionSystem : Node
    {
        private static readonly ILogger logger = LogFactory.GetLogger<SyncPositionSystem>();

        // todo make this work with network Visibility
        // todo add maxMessageSize (splits up update message into multiple messages if too big)
        // todo test sync interval vs fixed hz 

        [Export] public NetworkClient Client;
        [Export] public NetworkServer Server;

        public SyncSettings PackSettings = new SyncSettings();
        [NonSerialized] public SyncPacker packer;

        //[Tooltip("What channel to send messages on")]
        public Mirage.Channel MessageChannel = Channel.Unreliable;

        [ExportGroup("Synchronization Settings")]
        //[Tooltip("How many updates to perform per second. For best performance, set to a value below your maximum frame rate.")]
        public float SyncRate = 20;
        public float FixedSyncInterval => 1 / SyncRate;

        private Timer timer;
        [Export] private float syncTimer;

        [ExportGroup("Snapshot Interpolation")]
        //[Tooltip("Number of ticks to delay interpolation to make sure there is always a snapshot to interpolate towards. High delay can handle more jitter, but adds latancy to the position.")]
        public float TickDelayCount = 2;

        //[Tooltip("How much to modify the timescale of interpolation clock when ahead or behind the server")]
        public float TimeScaleModifier = 0.01f;

        //[Tooltip("SendToAll option skips visibility and sends position to all ready connections.")]
        public SyncMode syncMode = SyncMode.SendToAll;

        // cached object for update list
        private HashSet<SyncPositionBehaviour> dirtySet = new HashSet<SyncPositionBehaviour>();
        private HashSet<SyncPositionBehaviour> toUpdateObserverCache = new HashSet<SyncPositionBehaviour>();

        public SyncPositionBehaviourCollection Behaviours { get; private set; }

        [NonSerialized] private InterpolationTime _timeSync;
        public InterpolationTime TimeSync
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _timeSync;
        }

        public bool ClientActive => Client?.Active ?? false;
        public bool ServerActive => Server?.Active ?? false;

        public override void _Ready()
        {
            base._Ready();
            _timeSync = new InterpolationTime(1 / SyncRate, tickDelay: TickDelayCount, timeScale: TimeScaleModifier);
            packer = new SyncPacker(PackSettings);
            Behaviours = new SyncPositionBehaviourCollection(PackSettings);

            Server?.Started.AddListener(ServerStarted);
            Client?.Started.AddListener(ClientStarted);

            Server?.Stopped.AddListener(ServerStopped);
            Client?.Disconnected.AddListener(ClientStopped);
            OnValidate();
        }

        private void OnValidate()
        {
            packer = new SyncPacker(PackSettings ?? new SyncSettings());
            if (_timeSync != null)
                _timeSync.ClientDelay = TickDelayCount / SyncRate;
        }
        private void OnDestroy()
        {
            Server?.Started.RemoveListener(ServerStarted);
            Client?.Started.RemoveListener(ClientStarted);
        }

        private void ClientStarted()
        {
            // not host
            if (!ServerActive)
                timer = new Timer();

            // reset time incase this is 2nd time client starts
            _timeSync.Reset();
            Client.MessageHandler.RegisterHandler<NetworkPositionMessage>(ClientHandleNetworkPositionMessage);
        }

        private void ServerStarted()
        {
            timer = new Timer();
            Server.MessageHandler.RegisterHandler<NetworkPositionSingleMessage>(ServerHandleNetworkPositionMessage, false);
        }

        private void ClientStopped(ClientStoppedReason arg0)
        {
            Cleanup();
        }

        private void ServerStopped()
        {
            Cleanup();
        }

        /// <summary>
        /// shared clean up for both server/client
        /// </summary>
        private void Cleanup()
        {
            // clear all just incase they fail to remove themselves
            Behaviours.ClearBehaviours();
        }


        #region Sync Server -> Client
        private void LateUpdate()
        {
            if (timer == null)
                return;

            syncTimer += timer.Delta;
            // fixed atmost once a frame
            // but always SyncRate per second
            if (syncTimer > FixedSyncInterval)
            {
                syncTimer -= FixedSyncInterval;
                ServerUpdate(timer.Now);
            }
        }

        public override void _Process(double delta)
        {
            base._Process(delta);
            if (timer == null)
                return;

            if (Behaviours == null) Behaviours = new SyncPositionBehaviourCollection(PackSettings);

            timer.Update();
            ClientUpdate(timer.Delta);
            LateUpdate();
        }

        private void ServerUpdate(double time)
        {
            if (!ServerActive) return;

            Benchmark.StartFrame();
            // syncs every frame, each Behaviour will track its own timer
            switch (syncMode)
            {
                case SyncMode.SendToAll:
                    SendUpdateToAll(time);
                    break;
                case SyncMode.SendToObservers_PlayerDirty:
                    SendUpdateToObservers_PlayerDirty(time);
                    break;
                case SyncMode.SendToObservers_PlayerDirty_PackOnce:
                    SendUpdateToObservers_PlayerDirty_PackOnce(time);
                    break;
                case SyncMode.SendToObservers_DirtyObservers:
                    SendUpdateToObservers_DirtyObservers(time);
                    break;
                case SyncMode.SendToDirtyObservers_PackOnce:
                    SendUpdateToObservers_DirtyObservers_PackOnce(time);
                    break;
            }
            Benchmark.EndFrame();

            // host mode
            // todo do we need this?
            if (ClientActive)
                TimeSync.OnMessage(time);
        }

        private void ClientUpdate(float deltaTime)
        {
            if (!ClientActive) return;

            TimeSync.OnUpdate(deltaTime);
        }

        internal void SendUpdateToAll(double time)
        {
            // dont send message if no behaviours
            if (Behaviours.Dictionary.Count == 0)
            {
                GD.Print("ASDSADEAW");
                return;
            }


            UpdateDirtySet();
            using (var writer = NetworkWriterPool.GetWriter())
            {
                packer.PackTime(writer, time);

                foreach (var behaviour in dirtySet)
                {
                    if (logger.LogEnabled())
                        logger.Log($"Time {time:0.000}, Packing {behaviour.Name}");

                    packer.PackNext(writer, behaviour);

                    // todo handle client authority updates better
                    behaviour.ClearNeedsUpdate();
                }

                var msg = new NetworkPositionMessage
                {
                    payload = writer.ToArraySegment()
                };
                Server.SendToAll(msg, excludeLocalPlayer: true, MessageChannel);
            }
        }

        /// <summary>
        /// Loops through all players, followed by all dirty objects and checks if the player object can see each one
        /// </summary>
        /// <param name="time"></param>
        internal void SendUpdateToObservers_PlayerDirty(double time)
        {
            // dont send message if no behaviours
            if (Behaviours.Dictionary.Count == 0)
                return;

            UpdateDirtySet();

            using (var writer = NetworkWriterPool.GetWriter())
            {
                foreach (var player in Server.Players)
                {
                    writer.Reset();

                    packer.PackTime(writer, time);
                    foreach (var behaviour in dirtySet)
                    {
                        if (!behaviour.Identity.observers.Contains(player))
                            continue;

                        packer.PackNext(writer, behaviour);
                    }

                    var msg = new NetworkPositionMessage
                    {
                        payload = writer.ToArraySegment()
                    };
                    player.Send(msg, MessageChannel);
                }
            }

            ClearDirtySet();
        }

        /// <summary>
        /// Loops through all players, followed by all dirty objects and checks if the player object can see each one
        /// ...except this one packs data once.
        /// </summary>
        /// <param name="time"></param>
        internal void SendUpdateToObservers_PlayerDirty_PackOnce(double time)
        {
            // dont send message if no behaviours
            if (Behaviours.Dictionary.Count == 0)
                return;

            UpdateDirtySet();
            NetworkWriterPool.Configure(100, 200);

            using (var writer = NetworkWriterPool.GetWriter())
            {
                foreach (var player in Server.Players)
                {
                    writer.Reset();

                    packer.PackTime(writer, time);
                    foreach (var behaviour in dirtySet)
                    {
                        if (!behaviour.Identity.observers.Contains(player))
                            continue;

                        var packed = GetWriterFromPool_Behaviours(behaviour);
                        writer.CopyFromWriter(packed);
                    }

                    var msg = new NetworkPositionMessage
                    {
                        payload = writer.ToArraySegment()
                    };
                    player.Send(msg, MessageChannel);
                }
            }

            foreach (var writer in writerPool_Behaviours.Values)
                writer.Release();

            writerPool_Behaviours.Clear();

            ClearDirtySet();
        }

        private Dictionary<SyncPositionBehaviour, PooledNetworkWriter> writerPool_Behaviours = new Dictionary<SyncPositionBehaviour, PooledNetworkWriter>();

        private PooledNetworkWriter GetWriterFromPool_Behaviours(SyncPositionBehaviour behaviour)
        {
            if (!writerPool_Behaviours.TryGetValue(behaviour, out var writer))
            {
                writer = NetworkWriterPool.GetWriter();
                writerPool_Behaviours[behaviour] = writer;
                packer.PackNext(writer, behaviour);
            }

            return writer;
        }

        /// <summary>
        /// Loops through all dirty objects, and then their observers and then writes that behaviouir to a cahced writer
        /// </summary>
        /// <param name="time"></param>
        internal void SendUpdateToObservers_DirtyObservers(double time)
        {
            // dont send message if no behaviours
            if (Behaviours.Dictionary.Count == 0)
                return;

            UpdateDirtySet();

            foreach (var behaviour in dirtySet)
            {
                foreach (var observer in behaviour.Identity.observers)
                {
                    var writer = GetWriterFromPool(time, observer);

                    packer.PackNext(writer, behaviour);
                }
            }

            foreach (var player in Server.Players)
            {
                var writer = GetWriterFromPool(time, player);

                var msg = new NetworkPositionMessage { payload = writer.ToArraySegment() };
                player.Send(msg, MessageChannel);
                writer.Release();
            }
            writerPool.Clear();

            ClearDirtySet();
        }

        /// <summary>
        /// Loops through all dirty objects, and then their observers and then writes that behaviouir to a cahced writer
        /// <para>But Packs once and copies bytes</para>
        /// </summary>
        /// <param name="time"></param>
        internal void SendUpdateToObservers_DirtyObservers_PackOnce(double time)
        {
            // dont send message if no behaviours
            if (Behaviours.Dictionary.Count == 0)
                return;

            UpdateDirtySet();
            using (var packWriter = NetworkWriterPool.GetWriter())
            {
                foreach (var behaviour in dirtySet)
                {
                    if (behaviour.Identity.observers.Count == 0)
                        continue;

                    packWriter.Reset();
                    packer.PackNext(packWriter, behaviour);

                    foreach (var observer in behaviour.Identity.observers)
                    {
                        var writer = GetWriterFromPool(time, observer);

                        writer.CopyFromWriter(packWriter);
                    }
                }
            }

            foreach (var player in Server.Players)
            {
                var writer = GetWriterFromPool(time, player);

                var msg = new NetworkPositionMessage { payload = writer.ToArraySegment() };
                player.Send(msg, MessageChannel);
                writer.Release();
            }
            writerPool.Clear();

            ClearDirtySet();
        }

        private Dictionary<NetworkPlayer, PooledNetworkWriter> writerPool = new Dictionary<NetworkPlayer, PooledNetworkWriter>();

        private PooledNetworkWriter GetWriterFromPool(double time, NetworkPlayer player)
        {
            if (!writerPool.TryGetValue(player, out var writer))
            {
                writer = NetworkWriterPool.GetWriter();
                packer.PackTime(writer, time);
                writerPool[player] = writer;
            }

            return writer;
        }

        private void UpdateDirtySet()
        {
            dirtySet.Clear();
            foreach (var behaviour in Behaviours.Dictionary.Values)
            {
                //if (!behaviour.NeedsUpdate())
                //    continue;

                dirtySet.Add(behaviour);
            }
        }

        private void ClearDirtySet()
        {
            foreach (var behaviour in dirtySet)
                behaviour.ClearNeedsUpdate();

            dirtySet.Clear();
        }

        internal void ClientHandleNetworkPositionMessage(NetworkPositionMessage msg)
        {
            // hostMode
            if (ServerActive)
                return;

            using (var reader = NetworkReaderPool.GetReader(msg.payload, null))
            {
                var time = packer.UnpackTime(reader);

                if (TimeSync.IsMessageOutOfOrder(time))
                {
                    if (logger.LogEnabled()) logger.Log($"Ignoring packet before it was received out of order");
                    // we dont want old packetes, we only care about most recent data so client can lerp towards it
                    return;
                }

                while (packer.TryUnpackNext(reader, out var id, out var pos, out var rot))
                {
                    if (Behaviours.Dictionary.TryGetValue(id, out var behaviour))
                        behaviour.ApplyOnClient(new TransformState(pos, rot), time);
                }

                TimeSync.OnMessage(time);
            }
        }
        #endregion

        #region Sync Client Auth -> Server

        /// <summary>
        /// Position from client to server
        /// </summary>
        /// <param name="arg1"></param>
        /// <param name="arg2"></param>
        internal void ServerHandleNetworkPositionMessage(NetworkPlayer _, NetworkPositionSingleMessage msg)
        {
            using (var reader = NetworkReaderPool.GetReader(msg.payload, null))
            {
                //float time = packer.UnpackTime(reader);
                packer.UnpackNext(reader, out var id, out var pos, out var rot);

                if (Behaviours.TryGetValue(id, out var behaviour))
                    // todo fix host mode time
                    behaviour.ApplyOnServer(new TransformState(pos, rot), timer.Now);
                else
                    if (logger.WarnEnabled())
                    logger.LogWarning($"Could not find a NetworkBehaviour with id {id}");
            }
        }
        #endregion


        public class Timer
        {
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private double _previous;
            private float _delta;
            private double _now;

            public float Delta
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _delta;
            }
            public double Now
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _now;
            }

            private double GetNow()
            {
                return (double)(stopwatch.Elapsed.TotalMilliseconds / 1000.0);
            }

            public void Update()
            {
                _now = GetNow();
                _delta = (float)(_now - _previous);
                _previous = _now;
            }
        }
    }

    [NetworkMessage]
    public struct NetworkPositionMessage
    {
        public ArraySegment<byte> payload;
    }
    [NetworkMessage]
    public struct NetworkPositionSingleMessage
    {
        public ArraySegment<byte> payload;
    }
}
