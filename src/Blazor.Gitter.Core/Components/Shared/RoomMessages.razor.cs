﻿using Blazor.Gitter.Core.Browser;
using Blazor.Gitter.Library;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Blazor.Gitter.Core.Components.Shared
{
    public class RoomMessagesBase : ComponentBase, IDisposable
    {
        [Inject] IJSRuntime JSRuntime { get; set; }
        [Inject] IChatApi GitterApi { get; set; }
        [Inject] ILocalisationHelper Localisation { get; set; }
        [Inject] IAppState State { get; set; }

        [Parameter] internal IChatRoom ChatRoom { get; set; }
        [Parameter] internal string UserId { get; set; }

        internal bool LoadingMessages;

        internal List<IChatMessage> Messages;
        SemaphoreSlim ssScroll = new SemaphoreSlim(1, 1);
        SemaphoreSlim ssFetch = new SemaphoreSlim(1, 1);
        bool IsFetchingOlder = false;
        bool NoMoreOldMessages = false;
        bool FirstLoad = true;
        internal bool Paused = true;
        CancellationTokenSource tokenSource;
        System.Timers.Timer RoomWatcher;
        IChatRoom LastRoom;

        protected override void OnInit()
        {
            base.OnInit();
            State.ActivityTimeout += ActivityTimeout;
            State.ActivityResumed += ActivityResumed;
        }

        private void ActivityResumed(object sender, EventArgs e)
        {
            StartRoomWatcher();
            Console.WriteLine("RESUMED");
        }

        private void ActivityTimeout(object sender, EventArgs e)
        {
            try
            {
                RoomWatcher?.Stop();
                Paused = true;
                Invoke(StateHasChanged);
                //Task.Delay(1);
                Console.WriteLine("PAUSED");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.GetBaseException().Message);
            }
        }

        protected override void OnAfterRender()
        {
            base.OnAfterRender();
            if (FirstLoad && Messages?.Count > 0)
            {
                FirstLoad = false;
                State.RecordActivity();
            }
        }

        protected override async Task OnParametersSetAsync()
        {
            await base.OnParametersSetAsync();
            if (!ChatRoom.Equals(LastRoom))
            {
                LoadingMessages = true;

                LastRoom = ChatRoom;
                RoomWatcher?.Stop();
                NoMoreOldMessages = false;
                IsFetchingOlder = false;
                Console.WriteLine("Loading room...");
                Messages = new List<IChatMessage>();
                StartRoomWatcher();

                LoadingMessages = false;
            }
        }

        private void StartRoomWatcher()
        {
            if (!(RoomWatcher is object))
            {
                tokenSource = new CancellationTokenSource();
                RoomWatcher = new System.Timers.Timer(250);
                RoomWatcher.Elapsed += async (s, e) => await MonitorNewMessages();
            }
            RoomWatcher.Interval = 250;
            RoomWatcher.Start();
            Paused = false;
            Invoke(StateHasChanged);
            Task.Delay(1);
        }

        internal async Task MessagesScrolled(UIEventArgs args)
        {
            if (!NoMoreOldMessages && !IsFetchingOlder && Messages.Any())
            {
                await ssScroll.WaitAsync();
                try
                {
                    var scroll = await JSRuntime.GetScrollTop("blgmessagelist");
                    if (scroll < 100)
                    {
                        IsFetchingOlder = true;

                        var count = await FetchOldMessages(tokenSource.Token);
                        if (count == 0)
                        {
                            NoMoreOldMessages = true;
                        }
                        IsFetchingOlder = false;
                    }
                }
                catch
                {
                }
                finally
                {
                    ssScroll.Release();
                }
                State.RecordActivity();
            }
        }

        async Task MonitorNewMessages()
        {
            RoomWatcher.Stop();
            if (RoomWatcher.Interval == 250)
            {
                RoomWatcher.Interval = 2000;
            }
            var options = GitterApi.GetNewOptions();
            options.Lang = Localisation.LocalCultureInfo.Name;
            options.AfterId = "";

            bool bottom = false;
            try
            {
                bottom = await JSRuntime.IsScrolledToBottom("blgmessagelist");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }

            if (Messages?.Any() ?? false)
            {
                options.AfterId = GetLastMessageId();
            }
            await FetchNewMessages(options, tokenSource.Token);

            if (Messages?.Any() ?? false)
            {
                if (bottom)
                {
                    _ = await JSRuntime.ScrollIntoView(GetLastMessageId());
                }
            }
            RoomWatcher?.Start();
        }

        async Task<int> FetchNewMessages(IChatMessageOptions options, CancellationToken token)
        {
            IEnumerable<IChatMessage> messages = null;
            int count = 0;
            if (!token.IsCancellationRequested)
            {
                await ssFetch.WaitAsync(token);
                try
                {
                    messages = await GitterApi.GetChatMessages(ChatRoom.Id, options);
                    if (messages is object)
                    {
                        count = messages.Count();
                        if (!string.IsNullOrWhiteSpace(options.BeforeId))
                        {
                            Messages.InsertRange(0, messages);
                        }
                        else
                        {
                            Messages.AddRange(messages);
                        }
                        await Invoke(StateHasChanged);
                        await Task.Delay(1);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
                finally
                {
                    ssFetch.Release();
                }
            }
            return count;
        }

        async Task<int> FetchOldMessages(CancellationToken token)
        {
            var options = GitterApi.GetNewOptions();
            options.Lang = Localisation.LocalCultureInfo.Name;
            if (!token.IsCancellationRequested && IsFetchingOlder)
            {
                options.AfterId = "";
                if (Messages?.Any() ?? false)
                {
                    options.BeforeId = GetFirstMessageId();
                    var count = await FetchNewMessages(options, token);
                    await Invoke(StateHasChanged);
                    await Task.Delay(100);
                    _ = await JSRuntime.ScrollIntoView(options.BeforeId);
                    return count;
                }
            }
            token.ThrowIfCancellationRequested();
            return 0;
        }

        private string GetFirstMessageId()
        {
            return Messages.OrderBy(m => m.Sent).First().Id;
        }

        private string GetLastMessageId()
        {
            return Messages?.OrderBy(m => m.Sent).Last()?.Id ?? "";
        }
        public void Dispose()
        {
            RoomWatcher?.Stop();
            RoomWatcher?.Dispose();
            Messages = null;
            RoomWatcher = null;
        }
    }
}
