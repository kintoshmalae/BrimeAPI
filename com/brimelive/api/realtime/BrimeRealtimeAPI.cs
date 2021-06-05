﻿#nullable enable

using System.Collections.Generic;
using System.Threading.Tasks;
using IO.Ably;
using IO.Ably.Realtime;
using Newtonsoft.Json.Linq;

namespace BrimeAPI.com.brimelive.api.realtime {
    public class BrimeRealtimeAPI {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly string ABLY_TOKEN = "TTKZpg.6NfkQA:gAXhL8RYoA8iQ2o7";
        public static readonly int VIEW_UPDATE = 300000;        // re-sync viewer count every 5 minutes (realtime updated by ENTER/LEAVE messages)

        private AblyRealtime ably;

        private HashSet<BrimeRealtimeListener> listeners = new HashSet<BrimeRealtimeListener>(2); // normally will only have a single listener, plus the view tracker

        private bool isConnected = false;

        public ViewCountTracker ViewCountTracker { get; private set; }

        public BrimeRealtimeAPI(string channelName) {
            ClientOptions options = new ClientOptions(ABLY_TOKEN);
            options.AutoConnect = false;
            options.LogHandler = new AblyLogHandler(Logger);
            options.LogLevel = LogLevel.None;
            this.ably = new AblyRealtime(options);

            this.ably.Connection.On((state) => {
                // onOpen / onClose
                switch (state.Current) {
                    case IO.Ably.Realtime.ConnectionState.Connected:
                        isConnected = true;
                        foreach (BrimeRealtimeListener listener in listeners) listener.onOpen();
                        break;

                    case IO.Ably.Realtime.ConnectionState.Closed:
                    case IO.Ably.Realtime.ConnectionState.Failed:
                        isConnected = false;
                        foreach (BrimeRealtimeListener listener in listeners) listener.onClose();
                        break;
                }
            });

            this.ably.Channels.Get(channelName.ToLower() + "/alerts").Subscribe((message) => {
                // Follow Alerts
                if (message.Name == "alert") {
                    JObject data = JObject.Parse((string)message.Data);
                    string? type = data.Value<string>("type");
                    if (type != null) {
                        if (type.Equals("follow")) {
                            string? follower = data.Value<string>("follower");
                            string? followerID = data.Value<string>("followerID");
                            if (follower == null) follower = "";
                            if (followerID == null) followerID = "";
                            foreach (BrimeRealtimeListener listener in listeners) listener.onFollow(follower, followerID);
                        } else if (type.Equals("subscribe")) {
                            string? subscriber = data.Value<string>("subscriber");
                            string? subscriberID = data.Value<string>("subscriberID");
                            if (subscriber == null) subscriber = "";
                            if (subscriberID == null) subscriberID = "";
                            foreach (BrimeRealtimeListener listener in listeners) listener.onSubscribe(subscriber, subscriberID, false);
                        } else if (type.Equals("resubscribe")) {
                            string? subscriber = data.Value<string>("subscriber");
                            string? subscriberID = data.Value<string>("subscriberID");
                            if (subscriber == null) subscriber = "";
                            if (subscriberID == null) subscriberID = "";
                            foreach (BrimeRealtimeListener listener in listeners) listener.onSubscribe(subscriber, subscriberID, true);
                        }
                    }
                }
            });

            IRealtimeChannel channel = this.ably.Channels.Get(channelName.ToLower());

            IEnumerable<PresenceMessage> msgs = channel.Presence.GetAsync().Result;
            int viewCount = 0; foreach (PresenceMessage msg in msgs) viewCount++;
            ViewCountTracker = new ViewCountTracker(viewCount);
            registerListener(ViewCountTracker); // register for tracking updates

            Task.Run(() => {
                while (isConnected) {
                    System.Threading.Thread.Sleep(VIEW_UPDATE);
                    IEnumerable<PresenceMessage> msgs = channel.Presence.GetAsync().Result;
                    int viewCount = 0; foreach (PresenceMessage msg in msgs) viewCount++;
                    ViewCountTracker.ViewCount = viewCount;
                }
            });

            channel.Presence.Subscribe((message) => {
                // onJoin / onLeave
                switch (message.Action) {
                    case PresenceAction.Absent:
                    case PresenceAction.Leave:
                        foreach (BrimeRealtimeListener listener in listeners) listener.onLeave(message.ClientId);
                        break;

                    case PresenceAction.Enter:
                    case PresenceAction.Present:
                    case PresenceAction.Update:
                        foreach (BrimeRealtimeListener listener in listeners) listener.onJoin(message.ClientId);
                        break;
                }
            });

            // Deprecated
            channel.Subscribe((message) => {
                // onChat
                if (message.Name == "greeting") {
                    Logger.Trace("Message Data: " + message.Data);
                    BrimeChatMessage chatMessage = new BrimeChatMessage(JObject.Parse((string)message.Data));
                    foreach (BrimeRealtimeListener listener in listeners) listener.onChat(chatMessage);
                } else {
                    Logger.Trace("Message Name: " + message.Name);
                }
                /* TODO: Needs ChannelID, so will need Client-ID to use
                switch (message.Name) {
                    case "chat":    // updated chat messages
                        Logger.Trace("Chat Data: " + message.Data);
                        break;
                    case "delete":  // delete message
                        Logger.Trace("Delete Data: " + message.Data);
                        break;
                }
                */
            });
        }

        public void registerListener(BrimeRealtimeListener realtimeListener) {
            if (listeners.Contains(realtimeListener)) listeners.Remove(realtimeListener);
            listeners.Add(realtimeListener);
        }

        public void removeListener(BrimeRealtimeListener realtimeListener) {
            if (listeners.Contains(realtimeListener)) listeners.Remove(realtimeListener);
        }

        public void connect() {
            Logger.Trace("Connecting to Brime Realtime");
            this.ably.Connect();
        }

        public void close() {
            Logger.Trace("Disconnecting from Brime Realtime");
            this.ably.Close();
        }
    }
}

/*
need to use the new ably endpoint, "channelId/chat"
here's the new payloads schema for the new endpoint:
chat:
{
  _id: String,
  channelID: String,
  sender: User,
  message: String,
  richContents: String, // Used by brimebot.
  emotes: Emote[],
  timestamp: Number
}

delete:
{
  _id: String,
  messageID: String
}
[3:48 AM]
use the ably name field to get the type of message
*/
