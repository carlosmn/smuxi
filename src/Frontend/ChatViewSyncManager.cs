// Smuxi - Smart MUltipleXed Irc
//
// Copyright (c) 2011 Mirco Bauer <meebey@meebey.net>
//
// Full GPL License: <http://www.gnu.org/licenses/gpl.txt>
//
// This program is free software; you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation; either version 2 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA  02111-1307 USA

using System;
using System.Threading;
using System.Runtime.Remoting;
using System.Collections.Generic;
using Smuxi.Common;
using Smuxi.Engine;

namespace Smuxi.Frontend
{
    public class ChatViewSyncManager
    {
#if LOG4NET
        private static readonly log4net.ILog Logger = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
#endif
        ThreadPoolQueue WorkerQueue { set; get; }
        Dictionary<object, AutoResetEvent> SyncWaitQueue { set; get; }
        Dictionary<object, IChatView> SyncReleaseQueue { set; get; }

        public event EventHandler<ChatViewAddedEventArgs>  ChatAdded;
        public event EventHandler<ChatViewSyncedEventArgs> ChatSynced;

        public ChatViewSyncManager()
        {
            WorkerQueue = new ThreadPoolQueue() {
                MaxWorkers = 4
            };
            SyncWaitQueue = new Dictionary<object, AutoResetEvent>();
            SyncReleaseQueue = new Dictionary<object, IChatView>();
        }

        public void Add(ChatModel chatModel)
        {
            Trace.Call(chatModel);

            if (chatModel == null) {
                throw new ArgumentNullException("chatModel");
            }

#if LOG4NET
            DateTime start = DateTime.UtcNow;
#endif
            // REMOTING CALL 1
            var chatId = chatModel.ID;
            // REMOTING CALL 2
            var chatType = chatModel.ChatType;
            // REMOTING CALL 3
            var chatPosition = chatModel.Position;
            // REMOTING CALL 4
            IProtocolManager protocolManager = chatModel.ProtocolManager;
            Type protocolManagerType = null;
            if (protocolManager != null) {
                protocolManagerType = chatModel.ProtocolManager.GetType();
            }
#if LOG4NET
            DateTime stop = DateTime.UtcNow;
            double duration = stop.Subtract(start).TotalMilliseconds;
            Logger.Debug("Add() done, syncing took: " +
                         Math.Round(duration) + " ms");
#endif

            OnChatAdded(chatModel, chatId, chatType, chatPosition,
                        protocolManagerType);
        }

        public void Sync(IChatView chatView)
        {
            Trace.Call(chatView);

            if (chatView == null) {
                throw new ArgumentNullException("chatView");
            }

#if LOG4NET
            DateTime start = DateTime.UtcNow;
#endif
            chatView.Sync();
#if LOG4NET
            DateTime stop = DateTime.UtcNow;
            double duration = stop.Subtract(start).TotalMilliseconds;
            Logger.Debug("Sync() <" + chatView.ID + ">.Sync() done, " +
                         " syncing took: " + Math.Round(duration) + " ms");
#endif

            OnChatSynced(chatView);
        }

        /// <remarks>
        /// This method is thread safe.
        /// </remarks>
        public void QueueAdd(ChatModel chatModel)
        {
            Trace.Call(chatModel);

            if (chatModel == null) {
                throw new ArgumentNullException("chatModel");
            }

            var chatKey = GetChatKey(chatModel);
            lock (SyncWaitQueue) {
                SyncWaitQueue.Add(chatKey, new AutoResetEvent(false));
#if LOG4NET
                Logger.Debug("QueueAdd() <" + chatKey + "> created sync lock");
#endif
            }
            WorkerQueue.Enqueue(delegate {
                AddWorker(chatModel);
            });
        }

        /// <remarks>
        /// This method is thread safe.
        /// </remarks>
        public void QueueSync(ChatModel chatModel)
        {
            Trace.Call(chatModel);

            if (chatModel == null) {
                throw new ArgumentNullException("chatModel");
            }

            WorkerQueue.Enqueue(delegate {
                SyncWorker(chatModel);
            });
        }

        /// <remarks>
        /// This method is thread safe.
        /// </remarks>
        public void ReleaseSync(IChatView chatView)
        {
            Trace.Call(chatView);

            if (chatView == null) {
                throw new ArgumentNullException("chatView");
            }

            var chatKey = GetChatKey(chatView.ChatModel);
#if LOG4NET
            Logger.Debug("ReleaseSync() <" + chatKey + "> releasing " +
                         "<" + chatView.ID + ">");
#endif
            lock (SyncReleaseQueue) {
                SyncReleaseQueue.Add(chatKey, chatView);
            }
            AutoResetEvent syncWait = null;
            lock (SyncWaitQueue) {
                SyncWaitQueue.TryGetValue(chatKey, out syncWait);
            }
            // release the sync worker
            syncWait.Set();
        }

        public void Clear()
        {
            Trace.Call();

            lock (SyncWaitQueue)
            lock (SyncReleaseQueue) {
                SyncWaitQueue.Clear();
                SyncReleaseQueue.Clear();
            }
        }

        object GetChatKey(ChatModel chatModel)
        {
            if (RemotingServices.IsTransparentProxy(chatModel)) {
                // HACK: we can't use ChatModel as Dictionary as it is
                // a remoting object
                return RemotingServices.GetObjectUri(chatModel);
            }
            return chatModel;
        }

        void AddWorker(ChatModel chatModel)
        {
            try {
                Add(chatModel);
            } catch (Exception ex) {
#if LOG4NET
                Logger.Error("AddWorker(): Add() threw exception!" , ex);
#endif
            }
        }

        void SyncWorker(ChatModel chatModel)
        {
            try {
                var chatKey = GetChatKey(chatModel);
                AutoResetEvent syncWait = null;
                lock (SyncWaitQueue) {
                    SyncWaitQueue.TryGetValue(chatKey, out syncWait);
                }
                if (syncWait != null) {
#if LOG4NET
                    Logger.Debug("SyncWorker() <" + chatKey + "> waiting for " +
                                "sync lock release...");
#endif
                    // This chat was queued by QueueAdd() thus we need to wait
                    // till the ChatView is created and ready to be synced
                    syncWait.WaitOne();
#if LOG4NET
                    Logger.Debug("SyncWorker() <" + chatKey + "> " +
                                 "sync lock released");
#endif

                    // no longer need the sync lock
                    lock (SyncWaitQueue) {
                        SyncWaitQueue.Remove(chatKey);
                    }
                }

                IChatView chatView = null;
                lock (SyncReleaseQueue) {
                    SyncReleaseQueue.TryGetValue(chatKey, out chatView);
                }

                Sync(chatView);
            } catch (Exception ex) {
#if LOG4NET
                Logger.Error("SyncWorker(): Exception!", ex);
#endif
            }
        }

        void OnChatAdded(ChatModel chatModel, string chatId,
                         ChatType chatType, int chatPosition,
                         Type protocolManagerType)
        {
            if (ChatAdded != null) {
                ChatAdded(this,
                          new ChatViewAddedEventArgs(chatModel, chatId,
                                                     chatType, chatPosition,
                                                     protocolManagerType));
            }
        }

        void OnChatSynced(IChatView chatView)
        {
            if (ChatSynced != null) {
                ChatSynced(this, new ChatViewSyncedEventArgs(chatView));
            }
        }
    }

    public class ChatViewAddedEventArgs : EventArgs
    {
        public ChatModel ChatModel { get; private set; }
        public string ChatID { get; private set; }
        public ChatType ChatType { get; private set; }
        public int ChatPosition { get; private set; }
        public Type ProtocolManagerType { get; private set; }

        public ChatViewAddedEventArgs(ChatModel chatModel, string chatId,
                                      ChatType chatType, int chatPosition,
                                      Type protocolManagerType)
        {
            ChatModel = chatModel;
            ChatID = chatId;
            ChatType = chatType;
            ChatPosition = chatPosition;
            ProtocolManagerType = protocolManagerType;
        }
    }

    public class ChatViewSyncedEventArgs : EventArgs
    {
        public IChatView ChatView { get; private set; }

        public ChatViewSyncedEventArgs(IChatView chatView)
        {
            ChatView = chatView;
        }
    }
}
