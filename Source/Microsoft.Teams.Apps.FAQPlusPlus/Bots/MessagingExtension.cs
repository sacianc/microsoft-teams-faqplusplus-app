﻿// <copyright file="MessagingExtension.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>
namespace Microsoft.Teams.Apps.FAQPlusPlus.Bots
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights;
    using Microsoft.Bot.Builder;
    using Microsoft.Bot.Schema;
    using Microsoft.Bot.Schema.Teams;
    using Microsoft.Teams.Apps.FAQPlusPlus.Common.Models;
    using Microsoft.Teams.Apps.FAQPlusPlus.Models;
    using Microsoft.Teams.Apps.FAQPlusPlus.Services;
    using Newtonsoft.Json;

    /// <summary>
    /// Implements the logic of the messaging extension for FAQ++
    /// </summary>
    public class MessagingExtension
    {
        private const int TextTrimLengthForCard = 10;
        private const string SearchTextParameterName = "searchText";        // parameter name in the manifest file

        private readonly ISearchService searchService;
        private readonly TelemetryClient telemetryClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="MessagingExtension"/> class.
        /// </summary>
        /// <param name="searchService">searchService DI.</param>
        /// <param name="telemetryClient">telemetryClient DI.</param>
        public MessagingExtension(ISearchService searchService, TelemetryClient telemetryClient)
        {
            this.searchService = searchService;
            this.telemetryClient = telemetryClient;
        }

        /// <summary>
        /// Based on type of activity return the search results or error result.
        /// </summary>
        /// <param name="turnContext">turnContext for messaging extension.</param>
        /// <returns><see cref="Task"/> that returns an <see cref="InvokeResponse"/> with search results, or null to ignore the activity.</returns>
        public async Task<InvokeResponse> HandleMessagingExtensionQueryAsync(ITurnContext<IInvokeActivity> turnContext)
        {
            try
            {
                if (turnContext.Activity.Name == "composeExtension/query")
                {
                    var messageExtensionQuery = JsonConvert.DeserializeObject<MessagingExtensionQuery>(turnContext.Activity.Value.ToString());
                    var searchQuery = this.GetSearchQueryString(messageExtensionQuery);

                    return new InvokeResponse
                    {
                        Body = new MessagingExtensionResponse
                        {
                            ComposeExtension = await this.GetSearchResultAsync(searchQuery, messageExtensionQuery.CommandId, messageExtensionQuery.QueryOptions.Count, messageExtensionQuery.QueryOptions.Skip),
                        },
                        Status = 200,
                    };
                }
                else
                {
                    InvokeResponse response = null;
                    return response;
                }
            }
            catch (Exception ex)
            {
                this.telemetryClient.TrackTrace($"Failed to handle for ME activity: {ex.Message}", ApplicationInsights.DataContracts.SeverityLevel.Error);
                this.telemetryClient.TrackException(ex);
                throw;
            }
        }

        /// <summary>
        /// Get the results from Azure search service and populate the result (card + preview).
        /// </summary>
        /// <param name="query">query which the user had typed in message extension search.</param>
        /// <param name="commandId">commandId to determine which tab in message extension has been invoked.</param>
        /// <param name="count">count for pagination.</param>
        /// <param name="skip">skip for pagination.</param>
        /// <returns><see cref="Task"/> returns MessagingExtensionResult which will be used for providing the card.</returns>
        public async Task<MessagingExtensionResult> GetSearchResultAsync(string query, string commandId, int? count, int? skip)
        {
            MessagingExtensionResult composeExtensionResult = new MessagingExtensionResult
            {
                Type = "result",
                AttachmentLayout = "list",
                Attachments = new List<MessagingExtensionAttachment>(),
            };

            IList<TicketEntity> searchServiceResults = new List<TicketEntity>();

            // Enable prefix matches
            query = (query ?? string.Empty) + "*";

            // commandId should be equal to Id mentioned in Manifest file under composeExtensions section
            switch (commandId)
            {
                case "recents":
                    searchServiceResults = await this.searchService.SearchTicketsAsync(TicketSearchScope.RecentTickets, query, count, skip);
                    break;

                case "openrequests":
                    searchServiceResults = await this.searchService.SearchTicketsAsync(TicketSearchScope.OpenTickets, query, count, skip);
                    break;

                case "assignedrequests":
                    searchServiceResults = await this.searchService.SearchTicketsAsync(TicketSearchScope.AssignedTickets, query, count, skip);
                    break;
            }

            foreach (var searchResult in searchServiceResults)
            {
                var formattedResultTextForPreview = this.FormatSubTextForThumbnailCard(searchResult, true);
                ThumbnailCard previewCard = new ThumbnailCard
                {
                    Title = searchResult.AssignedToName,
                    Text = formattedResultTextForPreview,
                };

                var formattedResultTextForCard = this.FormatSubTextForThumbnailCard(searchResult, false);
                ThumbnailCard card = new ThumbnailCard
                {
                    Title = searchResult.AssignedToName,
                    Text = formattedResultTextForCard,
                };

                composeExtensionResult.Attachments.Add(card.ToAttachment().ToMessagingExtensionAttachment(previewCard.ToAttachment()));
            }

            return composeExtensionResult;
        }

        /// <summary>
        /// Format the text according to the card type which needs to be displayed.
        /// </summary>
        /// <param name="ticket">Ticket data to display.</param>
        /// <param name="isPreview">to determine if the formatting is for preview or card.</param>
        /// <returns>returns string which will be used in messaging extension.</returns>
        private string FormatSubTextForThumbnailCard(TicketEntity ticket, bool isPreview)
        {
            StringBuilder resultSubText = new StringBuilder();
            if (!string.IsNullOrEmpty(ticket.Title))
            {
                if (ticket.Title.Length > TextTrimLengthForCard && isPreview)
                {
                    resultSubText.Append("Request: " + ticket.Title.Substring(0, TextTrimLengthForCard) + "...");
                }
                else
                {
                    resultSubText.Append("Request: " + ticket.Title);
                }
            }

            if (ticket.Status == (int)TicketState.Open)
            {
                resultSubText.Append(" | " + TicketState.Open);
            }
            else
            {
                resultSubText.Append(" | " + TicketState.Closed);
            }

            if (ticket.DateCreated != null)
            {
                resultSubText.Append(" | " + ticket.DateCreated);
            }

            return resultSubText.ToString();
        }

        /// <summary>
        /// Returns query which the user has typed in message extension search.
        /// </summary>
        /// <param name="query">query typed by user in message extension.</param>
        /// <returns> returns user typed query.</returns>
        private string GetSearchQueryString(MessagingExtensionQuery query)
        {
            string messageExtensionInputText = string.Empty;
            foreach (var parameter in query.Parameters)
            {
                if (parameter.Name.Equals(SearchTextParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    messageExtensionInputText = parameter.Value.ToString();
                    break;
                }
            }

            return messageExtensionInputText;
        }
    }
}