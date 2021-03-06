(*---------------------------------------------------------------------------
    Copyright 2013 Microsoft

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.                                                      

    File: 
        DSetEnumerator.fs
  
    Description: 
        The Enumerator for DSet

    Author:
        Jin Li, Principal Researcher
        Microsoft Research, One Microsoft Way
        Email: jinl at microsoft dot com
    Date:
        Sept. 2013
    
 ---------------------------------------------------------------------------*)

namespace Prajna.Core

open System
open System.IO
open System.Collections
open System.Collections.Generic
open System.Collections.Concurrent
open System.Runtime.CompilerServices
open System.Threading
open System.Threading.Tasks
open System.Runtime.Serialization
open System.Linq
open Microsoft.FSharp.Collections
open Prajna.Tools
open Prajna.Tools.FSharp
open Prajna.Tools.StringTools


/// DSetTaskRead is a wrapper class to perform task control (e.g., readback metadata, map peer to DSet
[<AbstractClass>]
type internal DSetTaskRead( DSet: DSet ) = 
    inherit DSetAction()
    member val CurDSet = DSet with get
    member x.ReadInstance with get() = x.GetJobInstance( DSet ) 
    member x.PeerFailed with get() = x.ReadInstance.bPeerFailed
    member x.CurJob with get() = x.Job 
                     and set( job ) = x.Job <- job
//    member x.CurDSet with get() = Option.get (DSetFactory.Retrieve( curName  ))
//    override x.Finalize() =
//        DSetFactory.Remove( curName )
    /// Retrieve meta data
    member x.RetrieveMetaData() =
//        let curDSet = x.CurDSet
        let bMetadataReady = 
//            if Utils.IsNotNull curDSet.Function then 
            if x.CurDSet.Dependency <> DSetDependencyType.StandAlone then 
                // find a way to put the job in the name space that can be accessed. 
    //            let jobname = curDSet.Name
                if Utils.IsNull x.CurJob then 
                    x.Param <- x.CurDSet
                    x.InitializeJob()
                    x.CurJob.TypeOf <- JobTaskKind.ReadOne|||JobDependencies.DefaultTypeOfJobMask
                    
    //            JobFactory.Store( jobname, curJob )
    //            Already included in Param
    //            curJob.AddDSet( curDSet ) |> ignore
                x.BeginAction()
                let bSuccess = x.Job.ReadyStatus && x.CurDSet.NumPartitions>0 
                Logger.LogF( x.Job.JobID, LogLevel.MildVerbose, ( fun _ -> sprintf "DSetTaskRead Job %s:%s metadata & start: %A" x.CurJob.Name x.CurJob.VersionString bSuccess ))
                // Set peer availability 
                if bSuccess then 
                    let bMetaRead, bPeerF = x.CurJob.SetMetaDataAvailability( x.CurDSet )
                    x.ReadInstance.JobID <- x.CurJob.JobID
                    x.ReadInstance.SetMetaDataAvailability( bMetaRead, bPeerF)
                bSuccess
            else
                if true then 
                    //if not curDSet.bValidMetadata then 
                    // Read Only is the only operation that doesn't use Job
                    // This pass is needed to setup the correct data
                    x.CurDSet.bValidMetadata <- x.ReadInstance.RetrieveOneMetaData( x.Job.JobID )
                x.CurDSet.bValidMetadata
        if bMetadataReady then 
            // Retrieve some metadata. 
            x.ReadInstance.InitiatePartitionStatus()
            x.ReadInstance.InitiateCommandStatus()
        bMetadataReady
    member x.RemappingForReadTask()= 
        if Utils.IsNull x.CurJob then
            failwith "Standalone read is deprecated"
            x.ReadInstance.Remapping() 
        else
            x.RemappingDSet()

type internal PartitionObjectQueue<'U>() = 
    let internalQueue = ConcurrentQueue<'U>()
    let objsizeQueue = ConcurrentQueue<int>()
    let objsizeTotal = ref 0L
    member x.Enqueue( o: 'U, so ) = 
        internalQueue.Enqueue( o ) 
        objsizeQueue.Enqueue( so ) 
        Interlocked.Add( objsizeTotal, int64 so ) |> ignore
    member x.TryDequeue( result ) =
        let bExist = internalQueue.TryDequeue( result )
        if bExist then 
            // Should be able to dequeue one object size as well
            let objSize = ref 0
            while not (objsizeQueue.TryDequeue( objSize )) do 
                ()
            Interlocked.Add( objsizeTotal, int64 -(!objSize) ) |> ignore
        bExist
    member x.QueueSize with get() = !objsizeTotal
    member x.Count with get() = internalQueue.Count
    member x.IsEmpty with get() = internalQueue.IsEmpty

/// DSet is a distributed key-value set
/// It is one of the foundation class in Prajna for computing. 
/// The design of DSet follows HashMultiMap, which is a cascade Dictionary that allows multiple values per key
type internal DSetEnumerator<'U >( DSet: DSet ) =
    inherit DSetTaskRead( DSet )
    let nCloseCalled = ref 0 
    let mutable numItems = 0
    let mutable bStartedReading = false
    // partitionKeyValuesList is a list of keys & values to be retrieved from peer, and be presented to the enumerator. 
    let mutable partitionKeyValuesList = null // Array.init 0 ( fun _ -> List<'K*'V>() )

    let noReset() = raise (new System.NotSupportedException("DSetGBaseEnumerator doesn't support reset" ) )
    let notStarted() = raise (new System.InvalidOperationException("DSetGBaseEnumerator can't be restarted" ) ) 
    let alreadyFinished() = raise (new System.InvalidOperationException(" DSetGBaseEnumerator enumeration has already finished") )
    let check started = if not started then notStarted()
    // current partition to pull item from
    let mutable curr : ('U) option = None 
    // current partition that is in use
    let mutable currPartition = 0
    let mutable seenWriteDSet = null
    let codec = MetaFunction<'U>()
    let mutable bReadToEnd = false
    /// Start retrieving data from Prajna, the call will block until MetaData of DSet is retrieved
    member x.BeginRead() = 
        // Initial peer to read from. 
//        partitionReadFromPeers <- curDSet.Mapping |> Array.map ( fun partimapping -> 
//                                                                    let useMappingIdx = ref 0
//                                                                    while !useMappingIdx < partimapping.Length && bPeerFailed.[ partimapping.[!useMappingIdx] ] do
//                                                                        useMappingIdx := (!useMappingIdx) + 1
//                                                                    if !useMappingIdx < partimapping.Length then partimapping.[!useMappingIdx] else -1 
//                                                                    )
//        for parti=0 to partitionReadFromPeers.Length-1 do
//            if partitionReadFromPeers.[parti]<0 then 
//                let msg = sprintf "DSetEnumerator.BeginRead fail, can't find live peer to read partition %d" parti
//                Logger.Log(LogLevel.Error, msg)
//                failwith msg 
        Logger.LogF( x.Job.JobID, LogLevel.MildVerbose, fun _ -> sprintf "Start Read Task %A" x.Job.JobID )
        x.CurDSet.Cluster.RegisterCallback( x.Job.JobID, x.CurDSet.Name, x.CurDSet.Version.Ticks, 
                    [| ControllerCommand( ControllerVerb.Unknown, ControllerNoun.DSet);
                       ControllerCommand( ControllerVerb.Close, ControllerNoun.Partition);
                     |],
                                                            { new NetworkCommandCallback with 
                                                                member this.Callback( cmd, peeri, ms, jobID, name, verNumber, cl ) = 
                                                                    x.ReadDSetCallback( cmd, peeri, ms, jobID )
                                                            } )
        seenWriteDSet <- Array.init x.CurDSet.NumPartitions ( fun _ -> ConcurrentDictionary<_,_>() ) 
        codec.Reset()
        // Don't care about whether remapping is sent out. 
        x.RemappingForReadTask() |> ignore

    /// Called to shutdown the reading data from Prajna, note that normally, the read operation is 
    /// graceful shutdown automatically. EndRead is not absolutely necessary to be called. 
    member x.EndRead() = 
        x.GetJobInstance(x.CurDSet).ToClose()
        x.GetJobInstance(x.CurDSet).PartitionAnalysis()
        if (Utils.IsNotNull x.CurDSet.Cluster) then
            x.CurDSet.Cluster.UnRegisterCallback( x.Job.JobID,  [| ControllerCommand( ControllerVerb.Unknown, ControllerNoun.DSet);
                                                                                            ControllerCommand( ControllerVerb.Close, ControllerNoun.Partition);
                                                                                         |] )
        if Utils.IsNotNull x.CurJob then 
            if bReadToEnd then 
                x.OrderlyEndAction()
            else
                x.EndAction()
        ()
    
    /// After an enumerator is created or after the Reset method is called, the MoveNext method must be called to advance 
    /// the enumerator to the first element of the dataset before reading the value of the Current property; otherwise, 
    /// Current is undefined.
    member x.GetCurrent() = 
        match curr with 
        | Some( v ) ->
            v
        | None ->
            if not bStartedReading then 
                notStarted() 
            else
                alreadyFinished()
    /// MoveNext method must be called to advance the enumerator to the first element of the dataset before reading the 
    /// value of the Current property; otherwise, current is undefined.
    member x.DoMoveNext() = 
        try
            if not bStartedReading then 
                bStartedReading <- true
                // Start reading operation, block unitl metadata becomes available.  
                let bMetadataRetrieved = x.RetrieveMetaData()
                if bMetadataRetrieved && x.CurDSet.NumPartitions>0 then 
                    partitionKeyValuesList <- Array.init x.CurDSet.NumPartitions ( fun _ -> PartitionObjectQueue<'U>() )
                    x.BeginRead() 
                else
                    // When all element is read, we should reset the partitionKeyValuesList to null to signal that the end of 
                    // the DSet has been reached. 
                    Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "Set null to partitionKeyValuesList as bMetadataRetrieved is %A and number of partitions is %d"
                                                                   bMetadataRetrieved  x.CurDSet.NumPartitions )
                    partitionKeyValuesList <- null 
        
            let mutable bFind = false
            use jobAction = x.TryExecuteSingleJobAction()
            if Utils.IsNotNull jobAction then 
                while Utils.IsNotNull partitionKeyValuesList && not bFind && not jobAction.IsCancelled do
                    if currPartition<0 || currPartition>=partitionKeyValuesList.Length then 
                        currPartition <- 0
                    let startPartition = currPartition
                    let mutable bStopFind = false
                    while partitionKeyValuesList.[currPartition].Count=0 && not bStopFind do
                        currPartition<- ( currPartition + 1 ) % partitionKeyValuesList.Length
                        if currPartition = startPartition then 
                            bStopFind <- true
                    if partitionKeyValuesList.[currPartition].Count>0 then 
                        // Some object in the partition 
                        let objRef = ref Unchecked.defaultof<_>
                        bFind <- partitionKeyValuesList.[currPartition].TryDequeue( objRef )
                        if bFind then
                            curr <- Some (!objRef)
                            Logger.LogF( jobAction.JobID, LogLevel.ExtremeVerbose, fun _ -> sprintf "DoMoveNext read from partition %i: %A" currPartition !objRef)
                    if not bFind then 
                        // Doesn't find any K-V in this round. 
                        let bRemapping = x.RemappingForReadTask()
                        let bEnd = x.GetJobInstance(x.CurDSet).AllDSetsRead(  )
                        // It's possible that some final items arrive after we went through partitionKeyValuesList thought there're not items
                        // but before we call "AllDSetsRead". So bEnd is true but these final items were not read yet
                        let anyItem = 
                            partitionKeyValuesList |> Array.exists(fun l -> not l.IsEmpty)
                        if not anyItem then
                            if bEnd && not bRemapping then 
                                Logger.LogF( jobAction.JobID, LogLevel.WildVerbose, fun _ -> "DoMoveNext: completed")
                                // setting partitionKeyValuesList signals the end of the entire DSet Read 
                                partitionKeyValuesList <- null
                            else                
                                ThreadPoolWaitHandles.safeWaitOne( jobAction.WaitHandle, 5 ) |> ignore  
                        elif bEnd && not bRemapping then 
                            Logger.LogF( jobAction.JobID, LogLevel.WildVerbose, fun _ -> "DoMoveNext: find some final items")
            if not bFind then 
                bReadToEnd <- true
                x.CloseDSetEnumerator()
                if Utils.IsNotNull jobAction && jobAction.CanExecuteOrThrow then 
                    ()
            bFind
        with
        | ex -> 
            Logger.LogF( x.Job.JobID, LogLevel.Error, fun _ -> sprintf "[Can be remote, please examine] During read, DSet, caught a exception of %A " ex )
            reraise( )
    member val LastBlockedTime = null with get, set
    member x.ReadDSetCallback( cmd, peeri, msRcvd, jobID ) = 
        let bRetRef = ref true
        using ( x.TryExecuteSingleJobAction()) ( fun jobAction -> 
            if Utils.IsNull jobAction then 
                try 
                    match ( cmd.Verb, cmd.Noun ) with 
                    | ( ControllerVerb.Exception, ControllerNoun.DSet ) -> 
                        let ex = msRcvd.ReadException()
                        let showMsg = sprintf "[ReadDSetCallback, Job already cancelled] Job %A, DSet %s, receive exception from peer %d message: %A" jobID x.CurDSet.Name peeri ex
                        Logger.Log( LogLevel.Info, showMsg )
                    | _ -> 
                        Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "[(may be OK)ReadDSetCallback, Job already cancelled] Job %A, DSet %s, receive cmd %A from peer %d of %dB"
                                                                                jobID x.CurDSet.Name cmd peeri msRcvd.Length ) 
                with
                | ex -> 
                    Logger.Log( LogLevel.Info, ( sprintf "ReadDSetCallback, Job %A, failed to parse exception message cmd %A, peer %d, with exception %A" jobID cmd peeri ex )    )
            else
                try
                    let curDSet = x.CurDSet
                    let q = curDSet.Cluster.Queue( peeri )
                    match ( cmd.Verb, cmd.Noun ) with 
                    | ( ControllerVerb.Acknowledge, ControllerNoun.DSet ) ->
                        ()
                    | ( ControllerVerb.Get, ControllerNoun.ClusterInfo ) ->
                        using( new MemStream( 10240 ) ) ( fun msSend -> 
                            curDSet.Cluster.ClusterInfo.Pack( msSend )
                            let cmd = ControllerCommand( ControllerVerb.Set, ControllerNoun.ClusterInfo ) 
                            // Expediate delivery of Cluster Information to the receiver
                            q.ToSend( cmd, msSend, true ) 
                        )
                    | ( ControllerVerb.Write, ControllerNoun.DSet ) ->
                        let parti = msRcvd.ReadVInt32()
                        if partitionKeyValuesList.[parti].QueueSize > DeploymentSettings.MaxBytesPerPartitionBeforeBlocking then 
                            Logger.Do( LogLevel.MildVerbose, ( fun _ -> 
                               let utcNow = (PerfDateTime.UtcNow())
                               if Utils.IsNull x.LastBlockedTime then 
                                   x.LastBlockedTime <- Array.create x.CurDSet.Cluster.NumNodes DateTime.MinValue
                               if utcNow.Subtract( x.LastBlockedTime.[peeri] ).TotalSeconds > DeploymentSettings.MonitorClientBlockingTime then 
                                   x.LastBlockedTime.[peeri] <- utcNow   
                                   Logger.LogF( LogLevel.MildVerbose, ( fun _ -> 
                                      sprintf "Write, DSet blocked on peer %d, partition %d, queuesize = %d, count = %d"
                                          peeri parti partitionKeyValuesList.[parti].QueueSize partitionKeyValuesList.[parti].Count ))
                               ))
                            bRetRef := false
                        else    
                            let serial = msRcvd.ReadInt64()
                            let numElems = msRcvd.ReadVInt32()
                            let meta = BlobMetadata( parti, serial, numElems )
                            let refValue = ref Unchecked.defaultof<_>
                            if seenWriteDSet.[parti].TryGetValue( (serial, numElems), refValue ) then 
                                Logger.LogF( LogLevel.MildVerbose, ( fun _ -> sprintf "Duplicate DSet encountered for Read, DSet operation: peer %d partition %d, serial %d:%d" peeri parti serial numElems ))
                            else
                                seenWriteDSet.[parti].Item( (serial, numElems) ) <- true
                                try
                                    let _, elemArray = codec.DecodeFunc( meta, msRcvd )
                                    if numElems<>elemArray.Length then 
                                        Logger.LogF( LogLevel.Error, ( fun _ -> sprintf "Key & value array are of different length: %s, K-V:%d, %A" 
                                                                                   (MetaFunction.MetaString(meta)) elemArray.Length elemArray ))
                                    elif elemArray.Length > 0 then 
                                        Logger.LogF( LogLevel.WildVerbose, ( fun _ -> sprintf "Add DSet to retrieving list, from peer %d, partition %d, serial %d:%d" peeri parti serial numElems ))
                                        let totalSize = msRcvd.Length
                                        let objSize = int totalSize / elemArray.Length
                                        for i = 0 to elemArray.Length - 1 do
                                            partitionKeyValuesList.[parti].Enqueue( elemArray.[i], objSize )
                                    else
                                        Logger.LogF( LogLevel.WildVerbose, ( fun _ -> sprintf "Receive a DSet collection of 0 elements, from peer %d, partition %d, serial %d:%d" peeri parti serial numElems ))
                                with
                                | e -> 
                                    Logger.LogF( LogLevel.Error, ( fun _ -> sprintf "Poorly formatted write, DSet, partition %d, serial %d:%d, with exception %A"
                                                                                   parti serial numElems e ))
                            // Echo is required for flow control, to inform the sender that the information has been read. 
                            using( new MemStream( 1024 ) ) ( fun msEcho -> 
                                let retCmd = ControllerCommand( ControllerVerb.Echo, ControllerNoun.DSet )
                                msEcho.WriteGuid( jobID )
                                msEcho.WriteString( curDSet.Name )
                                msEcho.WriteInt64( curDSet.Version.Ticks )
                                q.ToSend( ControllerCommand( ControllerVerb.Echo, ControllerNoun.DSet ), msEcho ) 
                            )
                    | ( ControllerVerb.Close, ControllerNoun.Partition) ->
                        let parti = msRcvd.ReadVInt32()
                        let numError = msRcvd.ReadVInt32()
                        if numError = 0 then 
                            // Signal a certain partition is succesfully read
                            Logger.LogF( LogLevel.WildVerbose, ( fun _ -> sprintf "From peer %d, partition %d has been sucessfully read without error" peeri parti ))
                        else
                            Logger.LogF( LogLevel.MildVerbose, ( fun _ -> sprintf "From peer %d, partition %d reaches an end with %d errors" peeri parti numError ))
                        x.GetJobInstance(x.CurDSet).ProcessedPartition( peeri, parti, numError )
                    | ( ControllerVerb.Error, ControllerNoun.DSet )  
                    | ( ControllerVerb.Info, ControllerNoun.DSet ) ->
                        // Missing DSet can be due to the reason that no key has been ever written to the partition. 
                        let errorCode = enum<DSetErrorType>( msRcvd.ReadByte() )
                        match errorCode with 
                        | DSetErrorType.NonExistPartition ->
                            let numMissedPartitions = msRcvd.ReadVInt32() 
                            let notFindPartitions = Array.zeroCreate<int> numMissedPartitions
                            for i = 0 to notFindPartitions.Length-1 do
                                let parti = msRcvd.ReadVInt32( ) 
                                notFindPartitions.[i] <- parti
                            let bRemapped = x.GetJobInstance(x.CurDSet).NotExistPartitions( peeri, notFindPartitions )
                            if bRemapped then 
                                x.GetJobInstance(x.CurDSet).Remapping() |> ignore
                            Logger.Log( LogLevel.MediumVerbose, ( sprintf "RetrieveMetaDataCallback: peer %d, doesn't have partition %A" peeri numMissedPartitions )    )
                        | e ->
                            Logger.Log( LogLevel.WildVerbose, ( sprintf "RetrieveMetaDataCallback: Unexpected Error,DSet errorcode %A from peer %d, command %A" errorCode peeri cmd )    )
            
                    | ( ControllerVerb.Close, ControllerNoun.DSet ) ->
                        Logger.LogF( LogLevel.WildVerbose, (fun _ -> sprintf "Close DSet received from peer %d" peeri ))
                        // Don't actively close queue, as it may be used by following command. 
                        // curDSet.Cluster.CloseQueueAndRelease( peeri )
                        x.GetJobInstance(x.CurDSet).PeerCmdComplete( peeri )
                    | _ ->
                        Logger.Log( LogLevel.Info, ( sprintf "Unparsed command in processing ReadDSetCallback, cmd %A, peer %d" cmd peeri )    )
                    if x.GetJobInstance(x.CurDSet).IsRemapping() then 
                        x.GetJobInstance(x.CurDSet).Remapping() |> ignore
                with
                | ex ->
                    let msg = sprintf "Error in parsing ReadDSetCallback, Job %A cmd %A, peer %d" jobID cmd peeri
                    jobAction.EncounterExceptionAtCallback( ex, msg )
        )
        !bRetRef

    member x.CloseDSetEnumeratorOnce() = 
        Logger.LogF( LogLevel.MildVerbose, fun _ -> sprintf "End Read Task %A" x.Job.JobID)
        if bStartedReading then 
            x.EndRead()
        else
            x.CloseAndUnregister()
        partitionKeyValuesList <- null
    member x.CloseDSetEnumerator() = 
        if Interlocked.CompareExchange( nCloseCalled, 1, 0)=0 then 
            x.CloseDSetEnumeratorOnce()
    override x.Finalize() =
        x.CloseDSetEnumerator()
    interface IDisposable with
        member x.Dispose() = 
            x.CloseDSetEnumerator()
            GC.SuppressFinalize(x)

    interface System.Collections.IEnumerator with
        member x.Current = box(x.GetCurrent())
        member x.MoveNext() =
            x.DoMoveNext()
        member x.Reset() = 
            x.CloseDSetEnumerator() 
            noReset()

    interface IEnumerator<('U)> with
        member x.Current = x.GetCurrent()
