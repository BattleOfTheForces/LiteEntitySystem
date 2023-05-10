using System;
using System.Collections.Generic;
using System.Diagnostics;
using K4os.Compression.LZ4;
using LiteEntitySystem.Internal;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    /// <summary>
    /// Client entity manager
    /// </summary>
    public sealed class ClientEntityManager : EntityManager
    {
        /// <summary>
        /// Current interpolated server tick
        /// </summary>
        public ushort ServerTick { get; private set; }
        
        /// <summary>
        /// Current rollback tick (valid only in Rollback state)
        /// </summary>
        public ushort RollBackTick { get; private set; }

        /// <summary>
        /// Current state server tick
        /// </summary>
        public ushort RawServerTick => _stateA?.Tick ?? 0;

        /// <summary>
        /// Target state server tick
        /// </summary>
        public ushort RawTargetServerTick => _stateB?.Tick ?? RawServerTick;

        /// <summary>
        /// Our local player
        /// </summary>
        public NetPlayer LocalPlayer => _localPlayer;
        
        /// <summary>
        /// Stored input commands count for prediction correction
        /// </summary>
        public int StoredCommands => _inputCommands.Count;
        
        /// <summary>
        /// Player tick processed by server
        /// </summary>
        public ushort LastProcessedTick => _stateA?.ProcessedTick ?? 0;

        /// <summary>
        /// Last received player tick by server
        /// </summary>
        public ushort LastReceivedTick => _stateA?.LastReceivedTick ?? 0;

        /// <summary>
        /// Send rate of server
        /// </summary>
        public ServerSendRate ServerSendRate => _serverSendRate;
        
        /// <summary>
        /// States count in interpolation buffer
        /// </summary>
        public int LerpBufferCount => _readyStates.Count;
        
        private const int InputBufferSize = 128;

        private readonly NetPeer _localPeer;
        private readonly Queue<ServerStateData> _statesPool = new(MaxSavedStateDiff);
        private readonly Dictionary<ushort, ServerStateData> _receivedStates = new();
        private readonly SequenceBinaryHeap<ServerStateData> _readyStates = new(MaxSavedStateDiff);
        private readonly Queue<byte[]> _inputCommands = new (InputBufferSize);
        private readonly Queue<byte[]> _inputPool = new (InputBufferSize);
        private readonly Queue<(ushort id, EntityLogic entity)> _spawnPredictedEntities = new ();
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly byte[][] _predictedEntities = new byte[MaxSyncedEntityCount][];
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;

        private struct SyncCallInfo
        {
            public MethodCallDelegate OnSync;
            public InternalEntity Entity;
            public int PrevDataPos;
        }
        private SyncCallInfo[] _syncCalls;
        private readonly HashSet<SyncableField> _changedSyncableFields = new();
        private int _syncCallsCount;
        
        private InternalEntity[] _entitiesToConstruct = new InternalEntity[64];
        private int _entitiesToConstructCount;
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;
        private ushort _lastReadyTick;

        //adaptive lerp vars
        private float _adaptiveMiddlePoint = 3f;
        private readonly float[] _jitterSamples = new float[10];
        private int _jitterSampleIdx;
        private readonly Stopwatch _jitterTimer = new();
        
        //local player
        private NetPlayer _localPlayer;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="inputProcessor">Input processor (you can use default InputProcessor/<T/> or derive from abstract one to make your own input serialization</param>
        /// <param name="localPeer">Local NetPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ClientEntityManager(
            EntityTypesMap typesMap, 
            InputProcessor inputProcessor, 
            NetPeer localPeer, 
            byte headerByte, 
            byte framesPerSecond) : base(typesMap, inputProcessor, NetworkMode.Client, framesPerSecond, headerByte)
        {
            _localPeer = localPeer;
            _sendBuffer[0] = headerByte;
            _sendBuffer[1] = InternalPackets.ClientInput;

            AliveEntities.SubscribeToConstructed(OnAliveConstructed, false);
            AliveEntities.OnDestroyed += OnAliveDestroyed;

            for (int i = 0; i < MaxSavedStateDiff; i++)
            {
                _statesPool.Enqueue(new ServerStateData());
            }
        }

        private unsafe void OnAliveConstructed(InternalEntity entity)
        {
            ref var classData = ref ClassDataDict[entity.ClassId];

            if (classData.InterpolatedFieldsSize > 0)
            {
                Utils.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classData.InterpolatedFieldsSize);
                
                //for local interpolated
                Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], classData.InterpolatedFieldsSize);
            }

            fixed (byte* interpDataPtr = _interpolatedInitialData[entity.Id])
            {
                for (int i = 0; i < classData.InterpolatedCount; i++)
                {
                    var field = classData.Fields[i];
                    field.TypeProcessor.WriteTo(entity, field.Offset, interpDataPtr + field.FixedOffset);
                }
            }
        }

        private void OnAliveDestroyed(InternalEntity e)
        {
            if(e.Id < MaxSyncedEntityCount)
                _predictedEntities[e.Id] = null;
        }

        /// <summary>
        /// Read incoming data in case of first byte is == headerByte
        /// </summary>
        /// <param name="reader">Reader with data (will be recycled inside, also works with autorecycle)</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetDataReader reader)
        {
            if (reader.PeekByte() == _sendBuffer[0])
            {
                reader.SkipBytes(1);
                Deserialize(reader);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Read incoming data omitting header byte
        /// </summary>
        /// <param name="reader">NetDataReader with data</param>
        public unsafe void Deserialize(NetDataReader reader)
        {
            fixed (byte* rawData = reader.RawData)
                Deserialize(rawData + reader.UserDataOffset, reader.UserDataSize);
        }
        
        private unsafe void Deserialize(byte* rawData, int size)
        {
            byte packetType = rawData[1];
            if(packetType == InternalPackets.BaselineSync)
            {
                if (size < sizeof(BaselineDataHeader) + 2)
                    return;
                _entitiesToConstructCount = 0;
                _syncCallsCount = 0;
                _changedSyncableFields.Clear();
                //read header and decode
                int decodedBytes;
                var header = *(BaselineDataHeader*)rawData;
                _serverSendRate = (ServerSendRate)header.SendRate;
                
                //reset pooled
                if (_stateB != null)
                {
                    _statesPool.Enqueue(_stateB);
                    _stateB = null;
                }
                while (_readyStates.Count > 0)
                {
                    _statesPool.Enqueue(_readyStates.ExtractMin());
                }
                foreach (var stateData in _receivedStates.Values)
                {
                    _statesPool.Enqueue(stateData);
                }
                _receivedStates.Clear();

                _stateA ??= _statesPool.Dequeue();
                _stateA.Reset(header.Tick);
                _stateA.Size = header.OriginalLength;
                _stateA.Data = new byte[header.OriginalLength];
                InternalPlayerId = header.PlayerId;
                _localPlayer = new NetPlayer(_localPeer, InternalPlayerId);

                fixed (byte* stateData = _stateA.Data)
                {
                    decodedBytes = LZ4Codec.Decode(
                        rawData + sizeof(BaselineDataHeader),
                        size - sizeof(BaselineDataHeader),
                        stateData,
                        _stateA.Size);
                    if (decodedBytes != header.OriginalLength)
                    {
                        Logger.LogError("Error on decompress");
                        return;
                    }
                    int bytesRead = 0;
                    while (bytesRead < _stateA.Size)
                    {
                        ushort entityId = *(ushort*)(stateData + bytesRead);
                        //Logger.Log($"[CEM] ReadBaseline Entity: {entityId} pos: {bytesRead}");
                        bytesRead += sizeof(ushort);
                        
                        if (entityId == InvalidEntityId || entityId >= MaxSyncedEntityCount)
                        {
                            Logger.LogError($"Bad data (id > {MaxSyncedEntityCount} or Id == 0) Id: {entityId}");
                            return;
                        }

                        ReadEntityState(stateData, ref bytesRead, entityId, true, true);
                        if (bytesRead == -1)
                            return;
                    }
                }
                ServerTick = _stateA.Tick;
                _lastReadyTick = ServerTick;
                _inputCommands.Clear();
                _isSyncReceived = true;
                _jitterTimer.Restart();
                ConstructAndSync(true);
                Logger.Log($"[CEM] Got baseline sync. Assigned player id: {header.PlayerId}, Original: {decodedBytes}, Tick: {header.Tick}, SendRate: {_serverSendRate}");
            }
            else
            {
                if (size < sizeof(DiffPartHeader) + 2)
                    return;
                var diffHeader = *(DiffPartHeader*)rawData;
                int tickDifference = Utils.SequenceDiff(diffHeader.Tick, _lastReadyTick);
                if (tickDifference <= 0)
                {
                    //old state
                    return;
                }
                
                //sample jitter
                _jitterSamples[_jitterSampleIdx] = _jitterTimer.ElapsedMilliseconds / 1000f;
                _jitterSampleIdx = (_jitterSampleIdx + 1) % _jitterSamples.Length;
                //reset timer
                _jitterTimer.Reset();

                if (!_receivedStates.TryGetValue(diffHeader.Tick, out var serverState))
                {
                    if (_statesPool.Count == 0)
                    {
                        serverState = new ServerStateData { Tick = diffHeader.Tick };
                    }
                    else
                    {
                        serverState = _statesPool.Dequeue();
                        serverState.Reset(diffHeader.Tick);
                    }
                    
                    _receivedStates.Add(diffHeader.Tick, serverState);
                }
                if(serverState.ReadPart(diffHeader, rawData, size))
                {
                    if (Utils.SequenceDiff(serverState.LastReceivedTick, _lastReceivedInputTick) > 0)
                        _lastReceivedInputTick = serverState.LastReceivedTick;
                    if (Utils.SequenceDiff(serverState.Tick, _lastReadyTick) > 0)
                        _lastReadyTick = serverState.Tick;
                    _receivedStates.Remove(serverState.Tick);
                    _readyStates.Add(serverState, serverState.Tick);
                    PreloadNextState();
                }
            }
        }

        private bool PreloadNextState()
        {
            if (_stateB != null)
                return true;
            if (_readyStates.Count == 0)
            {
                if (_adaptiveMiddlePoint < 3f)
                    _adaptiveMiddlePoint = 3f;
                return false;
            }

            float jitterSum = 0f;
            bool adaptiveIncreased = false;
            for (int i = 0; i < _jitterSamples.Length - 1; i++)
            {
                float jitter = Math.Abs(_jitterSamples[i] - _jitterSamples[i + 1]) * FramesPerSecond;
                jitterSum += jitter;
                if (jitter > _adaptiveMiddlePoint)
                {
                    _adaptiveMiddlePoint = jitter;
                    adaptiveIncreased = true;
                }
            }

            if (!adaptiveIncreased)
            {
                jitterSum /= _jitterSamples.Length;
                _adaptiveMiddlePoint = Utils.Lerp(_adaptiveMiddlePoint, Math.Max(1f, jitterSum), 0.05f);
            }
            
            _stateB = _readyStates.ExtractMin();
            _stateB.Preload(EntitiesDict);
            //Logger.Log($"Preload A: {_stateA.Tick}, B: {_stateB.Tick}");
            _lerpTime = 
                Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF *
                (1f - (_readyStates.Count + 1 - _adaptiveMiddlePoint) * 0.02f);

            //remove processed inputs
            while (_inputCommands.Count > 0)
            {
                if (Utils.SequenceDiff(_stateB.ProcessedTick, (ushort)(_tick - _inputCommands.Count + 1)) >= 0)
                    _inputPool.Enqueue(_inputCommands.Dequeue());
                else
                    break;
            }

            return true;
        }

        private unsafe void GoToNextState()
        {
            _statesPool.Enqueue(_stateA);
            _stateA = _stateB;
            _stateB = null;
            
            //Logger.Log($"GotoState: IST: {ServerTick}, TST:{_stateA.Tick}");

            fixed (byte* readerData = _stateA.Data)
            {
                for (int i = 0; i < _stateA.PreloadDataCount; i++)
                {
                    ref var preloadData = ref _stateA.PreloadDataArray[i];
                    ReadEntityState(readerData, ref preloadData.DataOffset, preloadData.EntityId, preloadData.EntityFieldsOffset == -1, false);
                    if (preloadData.DataOffset == -1)
                        return;
                }
            }
            ConstructAndSync(false);
            
            _timer -= _lerpTime;

            //reset owned entities
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal)
                    continue;
                
                ref var classData = ref entity.GetClassData();
                if(entity.IsServerControlled && !classData.HasRemoteRollbackFields)
                    continue;

                fixed (byte* predictedData = _predictedEntities[entity.Id])
                {
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        if (!field.ShouldRollback(entity))
                            continue;
                        if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            var syncableField = Utils.RefFieldValue<SyncableField>(entity, field.Offset);
                            field.TypeProcessor.SetFrom(syncableField, field.SyncableSyncVarOffset, predictedData + field.PredictedOffset);
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(entity, field.Offset, predictedData + field.PredictedOffset);
                        }
                    }
                }
            }

            //reapply input
            UpdateMode = UpdateMode.PredictionRollback;
            RollBackTick = (ushort)(_tick - _inputCommands.Count + 1);
            foreach (var inputCommand in _inputCommands)
            {
                //reapply input data
                fixed (byte* rawInputData = inputCommand)
                {
                    var header = *(InputPacketHeader*)rawInputData;
                    _localPlayer.StateATick = header.StateA;
                    _localPlayer.StateBTick = header.StateB;
                    _localPlayer.LerpTime = header.LerpMsec;
                }
                InputProcessor.ReadInputs(this, _localPlayer.Id, inputCommand, sizeof(InputPacketHeader), inputCommand.Length);
                foreach (var entity in AliveEntities)
                {
                    if(entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    entity.Update();
                }
                RollBackTick++;
            }
            UpdateMode = UpdateMode.Normal;
            
            //update local interpolated position
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal || !entity.IsLocalControlled)
                    continue;
                
                ref var classData = ref entity.GetClassData();
                for(int i = 0; i < classData.InterpolatedCount; i++)
                {
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id])
                    {
                        ref var field = ref classData.Fields[i];
                        field.TypeProcessor.WriteTo(entity, field.Offset, currentDataPtr + field.FixedOffset);
                    }
                }
            }
            
            //delete predicted
            while (_spawnPredictedEntities.TryPeek(out var info))
            {
                if (Utils.SequenceDiff(_stateA.ProcessedTick, info.id) >= 0)
                {
                    _spawnPredictedEntities.Dequeue();
                    info.entity.DestroyInternal();
                }
                else
                {
                    break;
                }
            }
            
            //load next state
            double prevLerpTime = _lerpTime;
            if (PreloadNextState())
            {
                //adjust lerp timer
                _timer *= prevLerpTime / _lerpTime;
            }
        }

        protected override unsafe void OnLogicTick()
        {
            if (_stateB != null)
            {
                _logicLerpMsec = (float)(_timer / _lerpTime);
                ServerTick = Utils.LerpSequence(_stateA.Tick, (ushort)(_stateB.Tick-1), _logicLerpMsec);
                _stateB.ExecuteRpcs(this, _stateA.Tick, false);
            }

            if (_inputCommands.Count > InputBufferSize)
            {
                _inputCommands.Clear();
            }
            
            int maxResultSize = sizeof(InputPacketHeader) + InputProcessor.GetInputsSize(this);
            if (_inputPool.TryDequeue(out byte[] inputWriter))
                Utils.ResizeIfFull(ref inputWriter, maxResultSize);
            else
                inputWriter = new byte[maxResultSize];
            
            fixed(byte* writerData = inputWriter)
                *(InputPacketHeader*)writerData = new InputPacketHeader
                {
                    StateA   = _stateA.Tick,
                    StateB   = RawTargetServerTick,
                    LerpMsec = _logicLerpMsec
                };

            //generate inputs
            int offset = sizeof(InputPacketHeader);
            InputProcessor.GenerateAndWriteInputs(this, inputWriter, ref offset);

            //read
            InputProcessor.ReadInputs(this, _localPlayer.Id, inputWriter, sizeof(InputPacketHeader), offset);
            _inputCommands.Enqueue(inputWriter);

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                ref var classData = ref ClassDataDict[entity.ClassId];
                if (entity.IsLocal || entity.IsLocalControlled)
                {
                    //save data for interpolation before update
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                           prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        //restore previous
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.TypeProcessor.SetFrom(entity, field.Offset, currentDataPtr + field.FixedOffset);
                        }

                        //update
                        entity.Update();
                
                        //save current
                        RefMagic.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.TypeProcessor.WriteTo(entity, field.Offset, currentDataPtr + field.FixedOffset);
                        }
                    }
                }
                else if(classData.UpdateOnClient)
                {
                    entity.Update();
                }
            }
        }

        /// <summary>
        /// Update method, call this every frame
        /// </summary>
        public override unsafe void Update()
        {
            if (!_isSyncReceived)
                return;
            
            //logic update
            ushort prevTick = _tick;
            base.Update();

            if (PreloadNextState())
            {
                _timer += VisualDeltaTime;
                if (_timer >= _lerpTime)
                {
                    GoToNextState();
                }
            }

            if (_stateB != null)
            {
                //remote interpolation
                float fTimer = (float)(_timer/_lerpTime);
                for(int i = 0; i < _stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref _stateB.PreloadDataArray[_stateB.InterpolatedFields[i]];
                    var entity = EntitiesDict[preloadData.EntityId];
                    var fields = entity.GetClassData().Fields;
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr = _stateB.Data)
                    {
                        for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                        {
                            var interpolatedCache = preloadData.InterpolatedCaches[j];
                            var field = fields[interpolatedCache.Field];
                            field.TypeProcessor.SetInterpolation(
                                entity, 
                                field.Offset,
                                initialDataPtr + field.FixedOffset,
                                nextDataPtr + interpolatedCache.StateReaderOffset, 
                                fTimer);
                        }
                    }
                }
            }

            //local interpolation
            float localLerpT = LerpFactor;
            foreach (var entity in AliveEntities)
            {
                if (!entity.IsLocalControlled && !entity.IsLocal)
                    continue;
                
                ref var classData = ref entity.GetClassData();
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id], prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    for(int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        var field = classData.Fields[i];
                        field.TypeProcessor.SetInterpolation(
                            entity,
                            field.Offset,
                            prevDataPtr + field.FixedOffset,
                            currentDataPtr + field.FixedOffset,
                            localLerpT);
                    }
                }
            }

            //send buffered input
            if (_tick != prevTick)
            {
                //pack tick first
                int offset = 4;
                fixed (byte* sendBuffer = _sendBuffer)
                {
                    ushort currentTick = (ushort)(_tick - _inputCommands.Count + 1);
                    ushort tickIndex = 0;
                    
                    //Logger.Log($"SendingCommands start {_tick}");
                    foreach (byte[] inputCommand in _inputCommands)
                    {
                        if (Utils.SequenceDiff(currentTick, _lastReceivedInputTick) <= 0)
                        {
                            currentTick++;
                            continue;
                        }
                        fixed (byte* inputData = inputCommand)
                        {
                            if (offset + inputCommand.Length + sizeof(ushort) > _localPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
                            {
                                *(ushort*)(sendBuffer + 2) = currentTick;
                                _localPeer.Send(_sendBuffer, 0, offset, DeliveryMethod.Unreliable);
                                offset = 4;
                                
                                currentTick += tickIndex;
                                tickIndex = 0;
                            }
                            
                            //put size
                            *(ushort*)(sendBuffer + offset) = (ushort)(inputCommand.Length - sizeof(InputPacketHeader));
                            offset += sizeof(ushort);
                            
                            //put data
                            RefMagic.CopyBlock(sendBuffer + offset, inputData, (uint)inputCommand.Length);
                            offset += inputCommand.Length;
                        }

                        tickIndex++;
                        if (tickIndex == ServerEntityManager.MaxStoredInputs)
                        {
                            break;
                        }
                    }
                    *(ushort*)(sendBuffer + 2) = currentTick;
                    _localPeer.Send(_sendBuffer, 0, offset, DeliveryMethod.Unreliable);
                    _localPeer.NetManager.TriggerUpdate();
                }
            }
            
            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                entity.VisualUpdate();
            }
        }

        internal void AddOwned(EntityLogic entity)
        {
            if (entity.GetClassData().IsUpdateable && !entity.GetClassData().UpdateOnClient)
                AliveEntities.Add(entity);
        }
        
        internal void RemoveOwned(EntityLogic entity)
        {
            if (entity.GetClassData().IsUpdateable && !entity.GetClassData().UpdateOnClient)
                AliveEntities.Remove(entity);
        }

        internal void AddPredictedInfo(EntityLogic e)
        {
            _spawnPredictedEntities.Enqueue((_tick, e));
        }

        private void ConstructAndSync(bool firstSync)
        {
            //execute all previous rpcs
            ushort minimalTick = ServerTick;
            ServerTick = _stateA.Tick;
            _stateA.ExecuteRpcs(this, minimalTick, firstSync);

            //Call construct methods
            for (int i = 0; i < _entitiesToConstructCount; i++)
                ConstructEntity(_entitiesToConstruct[i]);
            _entitiesToConstructCount = 0;
            
            //Make OnSyncCalls
            foreach (var syncableField in _changedSyncableFields)
            {
                var entity = EntitiesDict[syncableField.ParentEntityId];
                entity.GetClassData().SyncableFields[syncableField.Id].OnSync?.Invoke(entity, syncableField);
            }
            _changedSyncableFields.Clear();
            for (int i = 0; i < _syncCallsCount; i++)
            {
                ref var syncCall = ref _syncCalls[i];
                syncCall.OnSync(syncCall.Entity, new ReadOnlySpan<byte>(_stateA.Data, syncCall.PrevDataPos, _stateA.Size-syncCall.PrevDataPos));
            }
            _syncCallsCount = 0;
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(ServerTick);
        }

        internal void MarkSyncableFieldChanged(SyncableField syncableField)
        {
            _changedSyncableFields.Add(syncableField);
        }

        private unsafe void ReadEntityState(byte* rawData, ref int readerPosition, ushort entityInstanceId, bool fullSync, bool fistSync)
        {
            var entity = EntitiesDict[entityInstanceId];
            
            //full sync
            if (fullSync)
            {
                byte version = rawData[readerPosition];
                ushort classId = *(ushort*)(rawData + readerPosition + 1);
                readerPosition += 3;

                //remove old entity
                if (entity != null && entity.Version != version)
                {
                    //this can be only on logics (not on singletons)
                    Logger.Log($"[CEM] Replace entity by new: {version}");
                    var entityLogic = (EntityLogic) entity;
                    if(!entityLogic.IsDestroyed)
                        entityLogic.DestroyInternal();
                    entity = null;
                }
                if(entity == null)
                {
                    //create new
                    entity = AddEntity(new EntityParams(classId, entityInstanceId, version, this));
                    ref var cd = ref ClassDataDict[entity.ClassId];
                    if(cd.PredictedSize > 0)
                        Utils.ResizeOrCreate(ref _predictedEntities[entity.Id], cd.PredictedSize);
                    if(cd.InterpolatedFieldsSize > 0)
                        Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], cd.InterpolatedFieldsSize);

                    Utils.ResizeIfFull(ref _entitiesToConstruct, _entitiesToConstructCount);
                    _entitiesToConstruct[_entitiesToConstructCount++] = entity;
                    //Logger.Log($"[CEM] Add entity: {entity.GetType()}");
                }
                else if(!fistSync)
                {
                    //skip full sync data if we already have correct entity
                    return;
                }
            }
            else if (entity == null)
            {
                Logger.LogError($"EntityNull? : {entityInstanceId}");
                readerPosition = -1;
                return;
            }
            
            ref var classData = ref entity.GetClassData();
            ref byte[] interpolatedInitialData = ref _interpolatedInitialData[entity.Id];
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            bool writeInterpolationData = entity.IsServerControlled || fullSync;
            Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);
            
            fixed (byte* interpDataPtr = interpolatedInitialData, predictedData = _predictedEntities[entity.Id])
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                        continue;
                    
                    ref var field = ref classData.Fields[i];
                    byte* readDataPtr = rawData + readerPosition;
                    
                    if(fullSync || field.ShouldRollback(entity))
                        RefMagic.CopyBlock(predictedData + field.PredictedOffset, readDataPtr, field.Size);
                    
                    if (field.FieldType == FieldType.SyncableSyncVar)
                    {
                        var syncableField = Utils.RefFieldValue<SyncableField>(entity, field.Offset);
                        field.TypeProcessor.SetFrom(syncableField, field.SyncableSyncVarOffset, readDataPtr);
                        if (classData.SyncableFields[syncableField.Id].OnSync != null)
                            _changedSyncableFields.Add(syncableField);
                    }
                    else
                    {
                        if (field.Flags.HasFlagFast(SyncFlags.Interpolated) && writeInterpolationData)
                        {
                            //this is interpolated save for future
                            RefMagic.CopyBlock(interpDataPtr + field.FixedOffset, readDataPtr, field.Size);
                        }

                        if (field.OnSync != null)
                        {
                            if (field.TypeProcessor.SetFromAndSync(entity, field.Offset, readDataPtr))
                                _syncCalls[_syncCallsCount++] = new SyncCallInfo
                                {
                                    OnSync = field.OnSync,
                                    Entity = entity,
                                    PrevDataPos = readerPosition
                                };
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(entity, field.Offset, readDataPtr);
                        }
                    }
                    //Logger.Log($"E {entity.Id} Field updated: {field.Name}");
                    readerPosition += field.IntSize;
                }
            }

            if (fullSync)
                _stateA.ReadRPCs(rawData, ref readerPosition, new EntitySharedReference(entity.Id, entity.Version), classData);
        }
    }
}