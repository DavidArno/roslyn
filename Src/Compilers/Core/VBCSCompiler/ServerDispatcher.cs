﻿// Copyright (c) Microsoft Open Technologies, Inc.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Roslyn.Utilities;
using System.Globalization;

namespace Microsoft.CodeAnalysis.CompilerServer
{
    /// <summary>
    /// The interface used by <see cref="ServerDispatcher"/> to dispatch requests.
    /// </summary>
    interface IRequestHandler
    {
        BuildResponse HandleRequest(BuildRequest req, CancellationToken cancellationToken);
    }

    /// <summary>
    /// This class handles the named pipe creation, listening, thread creation,
    /// and so forth. When a request comes in, it is dispatched on a new thread
    /// to the <see cref="IRequestHandler"/> interface. The request handler does the actual
    /// compilation. This class itself has no dependencies on the compiler.
    /// </summary>
    /// <remarks>
    /// One instance of this is created per process.
    /// </remarks>
    partial class ServerDispatcher
    {
        private class ConnectionData
        {
            public Task<CompletionReason> ConnectionTask;
            public Task<TimeSpan?> ChangeKeepAliveTask;

            internal ConnectionData(Task<CompletionReason> connectionTask, Task<TimeSpan?> changeKeepAliveTask)
            {
                ConnectionTask = connectionTask;
                ChangeKeepAliveTask = changeKeepAliveTask;
            }
        }

        /// <summary>
        /// Default time the server will stay alive after the last request disconnects.
        /// </summary>
        private static readonly TimeSpan DefaultServerKeepAlive = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Time to delay after the last connection before initiating a garbage collection
        /// in the server. 
        /// </summary>
        private static readonly TimeSpan GCTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Main entry point for the process. Initialize the server dispatcher
        /// and wait for connections.
        /// </summary>
        public static int Main(string[] args)
        {
            CompilerServerLogger.Initialize("SRV");
            CompilerServerLogger.Log("Process started");

            TimeSpan? keepAliveTimeout = null;

            try
            {
                int keepAliveValue;
                string keepAliveStr = ConfigurationManager.AppSettings["keepalive"];
                if (int.TryParse(keepAliveStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out keepAliveValue) &&
                    keepAliveValue >= 0)
                {
                    if (keepAliveValue == 0)
                    {
                        // This is a one time server entry.
                        keepAliveTimeout = null;
                    }
                    else
                    {
                        keepAliveTimeout = TimeSpan.FromSeconds(keepAliveValue);
                    }
                }
                else
                {
                    keepAliveTimeout = DefaultServerKeepAlive;
                }
            }
            catch (ConfigurationErrorsException e)
            {
                keepAliveTimeout = DefaultServerKeepAlive;
                CompilerServerLogger.LogException(e, "Could not read AppSettings");
            }

            CompilerServerLogger.Log("Keep alive timeout is: {0} milliseconds.", keepAliveTimeout?.TotalMilliseconds ?? 0);
            FatalError.Handler = FailFast.OnFatalException;

            var dispatcher = new ServerDispatcher(BuildProtocolConstants.PipeName, new CompilerRequestHandler());
            dispatcher.ListenAndDispatchConnections(keepAliveTimeout);
            return 0;
        }

        // Size of the buffers to use
        private const int PipeBufferSize = 0x10000;  // 64K

        private readonly string basePipeName;

        private readonly IRequestHandler handler;

        /// <summary>
        /// Create a new server that listens on the given base pipe name.
        /// When a request comes in, it is dispatched on a separate thread
        /// via the IRequestHandler interface passed in.
        /// </summary>
        /// <param name="basePipeName">Base name for named pipe</param>
        /// <param name="handler">Handler that handles requests</param>
        public ServerDispatcher(string basePipeName, IRequestHandler handler)
        {
            this.basePipeName = basePipeName;
            this.handler = handler;
        }

        /// <summary>
        /// This function will accept and process new connections until an event causes
        /// the server to enter a passive shut down mode.  For example if analyzers change
        /// or the keep alive timeout is hit.  At which point this function will cease 
        /// accepting new connections and wait for existing connections to complete before
        /// returning.
        /// </summary>
        public void ListenAndDispatchConnections(TimeSpan? keepAlive)
        {
            Debug.Assert(SynchronizationContext.Current == null);

            var isKeepAliveDefault = true;
            var connectionList = new List<ConnectionData>();
            Task analyzerTask = AnalyzerWatcher.CreateWatchFilesTask();
            Task gcTask = null;
            Task timeoutTask = null;
            Task<NamedPipeServerStream> listenTask = null;
            CancellationTokenSource listenCancellationTokenSource = null;

            do
            {
                // While this loop is running there should be an active named pipe listening for a 
                // connection.
                if (listenTask == null)
                {
                    Debug.Assert(listenCancellationTokenSource == null);
                    Debug.Assert(timeoutTask == null);
                    listenCancellationTokenSource = new CancellationTokenSource();
                    listenTask = CreateListenTask(listenCancellationTokenSource.Token);
                }

                // If there are no active clients running then the server needs to be in a timeout mode.
                if (connectionList.Count == 0 && timeoutTask == null && keepAlive.HasValue)
                {
                    Debug.Assert(listenTask != null);
                    timeoutTask = Task.Delay(keepAlive.Value);
                }

                WaitForAnyCompletion(connectionList, listenTask, timeoutTask, gcTask, analyzerTask);

                // If there is a connection event that has highest priority. 
                if (listenTask.IsCompleted)
                {
                    var changeKeepAliveSource = new TaskCompletionSource<TimeSpan?>();
                    connectionList.Add(new ConnectionData(CreateHandleConnectionTask(listenTask, changeKeepAliveSource), changeKeepAliveSource.Task));
                    listenTask = null;
                    listenCancellationTokenSource = null;
                    timeoutTask = null;
                    gcTask = null;
                    continue;
                }

                if ((timeoutTask != null && timeoutTask.IsCompleted) || analyzerTask.IsCompleted)
                {
                    listenCancellationTokenSource.Cancel();
                    break;
                }

                if (gcTask != null && gcTask.IsCompleted)
                {
                    gcTask = null;
                    GC.Collect();
                    continue;
                }

                // Only other option is a connection event.  Go ahead and clear out the dead connections
                if (!CheckConnectionTask(connectionList, ref keepAlive, ref isKeepAliveDefault))
                {
                    // If there is a client disconnection detected then the server needs to begin
                    // the shutdown process.  We have to assume that the client disconnected via
                    // Ctrl+C and wants the server process to terminate.  It's possible a compilation
                    // is running out of control and the client wants their machine back.  
                    listenCancellationTokenSource.Cancel();
                    break;
                }

                if (connectionList.Count == 0 && gcTask == null)
                {
                    gcTask = Task.Delay(GCTimeout);
                }

            } while (true);

            Task.WaitAll(connectionList.Select(x => x.ConnectionTask).ToArray());
        }

        /// <summary>
        /// The server farms out work to Task values and this method needs to wait until at least one of them
        /// has completed.
        /// </summary>
        private void WaitForAnyCompletion(IEnumerable<ConnectionData> e, params Task[] other)
        {
            var all = new List<Task>();
            all.AddRange(e.Select(x => x.ConnectionTask));
            all.AddRange(e.Select(x => x.ChangeKeepAliveTask).Where(x => x != null));
            all.AddRange(other.Where(x => x != null));
            Task.WaitAny(all.ToArray());
        }

        /// <summary>
        /// Checks the completed connection objects.
        /// </summary>
        /// <returns>True if everything completed normally and false if there were any client disconnections.</returns>
        private bool CheckConnectionTask(List<ConnectionData> connectionList, ref TimeSpan? keepAlive, ref bool isKeepAliveDefault)
        {
            var allFine = true;

            foreach (var current in connectionList)
            {
                if (current.ChangeKeepAliveTask != null && current.ChangeKeepAliveTask.IsCompleted)
                {
                    ChangeKeepAlive(current.ChangeKeepAliveTask, ref keepAlive, ref isKeepAliveDefault);
                    current.ChangeKeepAliveTask = null;
                }

                if (current.ConnectionTask.IsCompleted)
                {
                    if (current.ConnectionTask.Result == CompletionReason.ClientDisconnect)
                    {
                        allFine = false;
                    }
                }
            }

            // Finally remove any ConnectionData for connections which are no longer active.
            connectionList.RemoveAll(x => x.ConnectionTask.IsCompleted);

            return allFine;
        }

        private void ChangeKeepAlive(Task<TimeSpan?> task, ref TimeSpan? keepAlive, ref bool isKeepAliveDefault)
        {
            Debug.Assert(task.IsCompleted);
            if (task.Status != TaskStatus.RanToCompletion)
            {
                return;
            }

            var value = task.Result;
            if (value.HasValue)
            {
                if (isKeepAliveDefault || !keepAlive.HasValue || value.Value > keepAlive.Value)
                {
                    keepAlive = value;
                    isKeepAliveDefault = false;
                }
            }
        }

        /// <summary>
        /// Creates a Task that waits for a client connection to occur and returns the connected 
        /// <see cref="NamedPipeServerStream"/> object.  Throws on any connection error.
        /// </summary>
        /// <param name="cancellationToken">Used to cancel the connection sequence.</param>
        private async Task<NamedPipeServerStream> CreateListenTask(CancellationToken cancellationToken)
        {
            // Create the pipe and begin waiting for a connection. This 
            // doesn't block, but could fail in certain circumstances, such
            // as Windows refusing to create the pipe for some reason 
            // (out of handles?), or the pipe was disconnected before we 
            // starting listening.
            NamedPipeServerStream pipeStream = ConstructPipe();

            // Unfortunately the version of .Net we are using doesn't support the WaitForConnectionAsync
            // method.  When it is available it should absolutely be used here.  In the meantime we
            // have to deal with the idea that this WaitForConnection call will block a thread
            // for a significant period of time.  It is unadvisable to do this to a thread pool thread 
            // hence we will use an explicit thread here.
            var listenSource = new TaskCompletionSource<NamedPipeServerStream>();
            var listenTask = listenSource.Task;
            var listenThread = new Thread(() =>
            {
                try
                {
                    CompilerServerLogger.Log("Waiting for new connection");
                    pipeStream.WaitForConnection();
                    CompilerServerLogger.Log("Pipe connection detected.");

                    if (Environment.Is64BitProcess || MemoryHelper.IsMemoryAvailable())
                    {
                        CompilerServerLogger.Log("Memory available - accepting connection");
                        listenSource.SetResult(pipeStream);
                        return;
                    }

                    try
                    {
                        pipeStream.Close();
                    }
                    catch
                    {
                        // Okay for Close failure here.  
                    }

                    listenSource.SetException(new Exception("Insufficient resources to process new connection."));
                }
                catch (Exception ex)
                {
                    listenSource.SetException(ex);
                }
            });
            listenThread.Start();

            // Create a tasks that waits indefinitely (-1) and completes only when cancelled.
            var waitCancellationTokenSource = new CancellationTokenSource();
            var waitTask = Task.Delay(
                -1,
                CancellationTokenSource.CreateLinkedTokenSource(waitCancellationTokenSource.Token, cancellationToken).Token);
            await Task.WhenAny(listenTask, waitTask).ConfigureAwait(false);
            if (listenTask.IsCompleted)
            {
                waitCancellationTokenSource.Cancel();
                return await listenTask.ConfigureAwait(false);
            }

            // The listen operation was cancelled.  Close the pipe stream throw a cancellation exception to
            // simulate the cancel operation.
            waitCancellationTokenSource.Cancel();
            try
            {
                pipeStream.Close();
            }
            catch
            {
                // Okay for Close failure here.
            }

            throw new OperationCanceledException();
        }

        /// <summary>
        /// Creates a Task representing the processing of the new connection.  Returns null 
        /// if the server is unable to create a new Task object for the connection.  
        /// </summary>
        private async Task<CompletionReason> CreateHandleConnectionTask(Task<NamedPipeServerStream> pipeStreamTask, TaskCompletionSource<TimeSpan?> changeKeepAliveSource)
        {
            var pipeStream = await pipeStreamTask.ConfigureAwait(false);
            var clientConnection = new NamedPipeClientConnection(pipeStream);
            var connection = new Connection(clientConnection, this.handler);
            return await connection.ServeConnection(changeKeepAliveSource).ConfigureAwait(false);
        }

        /// <summary>
        /// Create an instance of the pipe. This might be the first instance, or a subsequent instance.
        /// There always needs to be an instance of the pipe created to listen for a new client connection.
        /// </summary>
        /// <returns>The pipe instance or throws an exception.</returns>
        private NamedPipeServerStream ConstructPipe()
        {
            // Add the process ID onto the pipe name so each process gets a unique pipe name.
            // The client must user this algorithm too to connect.
            string pipeName = basePipeName + Process.GetCurrentProcess().Id.ToString();

            CompilerServerLogger.Log("Constructing pipe '{0}'.", pipeName);

            SecurityIdentifier identifier = WindowsIdentity.GetCurrent().Owner;
            PipeSecurity security = new PipeSecurity();

            // Restrict access to just this account.  
            PipeAccessRule rule = new PipeAccessRule(identifier, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow);
            security.AddAccessRule(rule);
            security.SetOwner(identifier);

            NamedPipeServerStream pipeStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, // Maximum connections.
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.WriteThrough,
                PipeBufferSize, // Default input buffer
                PipeBufferSize, // Default output buffer
                security,
                HandleInheritability.None);

            CompilerServerLogger.Log("Successfully constructed pipe '{0}'.", pipeName);

            return pipeStream;
        }
    }
}
