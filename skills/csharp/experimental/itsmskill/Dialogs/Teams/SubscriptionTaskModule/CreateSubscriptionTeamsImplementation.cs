﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdaptiveCards;
using ITSMSkill.Extensions.Teams;
using ITSMSkill.Models;
using ITSMSkill.Models.UpdateActivity;
using ITSMSkill.Services;
using ITSMSkill.TeamsChannels;
using ITSMSkill.TeamsChannels.Invoke;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Schema;
using Microsoft.Bot.Schema.Teams;
using Microsoft.Bot.Solutions.Proactive;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace ITSMSkill.Dialogs.Teams.SubscriptionTaskModule
{
    /// <summary>
    /// CreateTicketTeamsImplementation Handles OnFetch and OnSumbit Activity for TaskModules
    /// </summary>
    [TeamsInvoke(FlowType = nameof(TeamsFlowType.CreateSubscription_Form))]
    public class CreateSubscriptionTeamsImplementation : ITeamsTaskModuleHandler<TaskModuleContinueResponse>
    {
        private readonly IStatePropertyAccessor<SkillState> _stateAccessor;
        private readonly IStatePropertyAccessor<ActivityReferenceMap> _activityReferenceMapAccessor;
        private readonly IStatePropertyAccessor<ActivityReferenceMap> _proactiveStateActivityReferenceMapAccessor;
        private readonly IStatePropertyAccessor<ConversationReferenceMap> _proactiveStateConversationReferenceMapAccessor;
        private readonly ConversationState _conversationState;
        private readonly BotSettings _settings;
        private readonly BotServices _services;
        private readonly IServiceManager _serviceManager;
        private readonly IConnectorClient _connectorClient;
        private readonly ITeamsActivity<AdaptiveCard> _teamsTicketUpdateActivity;
        private readonly IStatePropertyAccessor<ProactiveModel> _proactiveStateAccessor;
        private readonly ProactiveState _proactiveState;

        public CreateSubscriptionTeamsImplementation(
             IServiceProvider serviceProvider)
        {
            _conversationState = serviceProvider.GetService<ConversationState>();
            _settings = serviceProvider.GetService<BotSettings>();
            _services = serviceProvider.GetService<BotServices>();
            _stateAccessor = _conversationState.CreateProperty<SkillState>(nameof(SkillState));
            _serviceManager = serviceProvider.GetService<IServiceManager>();
            _connectorClient = serviceProvider.GetService<IConnectorClient>();
            _teamsTicketUpdateActivity = serviceProvider.GetService<ITeamsActivity<AdaptiveCard>>();
            _proactiveState = serviceProvider.GetService<ProactiveState>();
            _proactiveStateAccessor = _proactiveState.CreateProperty<ProactiveModel>(nameof(ProactiveModel));
            _activityReferenceMapAccessor = _conversationState.CreateProperty<ActivityReferenceMap>(nameof(ActivityReferenceMap));
            _proactiveStateActivityReferenceMapAccessor = _proactiveState.CreateProperty<ActivityReferenceMap>(nameof(ActivityReferenceMap));
            _proactiveStateConversationReferenceMapAccessor = _proactiveState.CreateProperty<ConversationReferenceMap>(nameof(ConversationReferenceMap));
        }

        public async Task<TaskModuleContinueResponse> OnTeamsTaskModuleFetchAsync(ITurnContext context, CancellationToken cancellationToken)
        {
            return new TaskModuleContinueResponse()
            {
                Value = new TaskModuleTaskInfo()
                {
                    Title = "ImpactTracker",
                    Height = "medium",
                    Width = 500,
                    Card = new Attachment
                    {
                        ContentType = AdaptiveCard.ContentType,
                        Content = SubscriptionTaskModuleAdaptiveCard.GetSubscriptionAdaptiveCard(_settings.MicrosoftAppId)
                    }
                }
            };
        }

        public async Task<TaskModuleContinueResponse> OnTeamsTaskModuleSubmitAsync(ITurnContext context, CancellationToken cancellationToken)
        {
            var state = await _stateAccessor.GetAsync(context, () => new SkillState());

            var activityValueObject = JObject.FromObject(context.Activity.Value);

            var isDataObject = activityValueObject.TryGetValue("data", StringComparison.InvariantCultureIgnoreCase, out JToken dataValue);
            JObject dataObject = null;
            if (isDataObject)
            {
                dataObject = dataValue as JObject;

                // Get filterUrgency
                var filterUrgency = dataObject.GetValue("UrgencyFilter");

                // Get filterName
                var filterName = dataObject.GetValue("FilterName");

                //// Get NotificationNamespace name
                var notificationNameSpace = dataObject.GetValue("NotificationNameSpace");

                //// Get filterName
                var postNotificationAPIName = dataObject.GetValue("PostNotificationAPIName");

                // Create Managemenet object
                var management = _serviceManager.CreateManagementForSubscription(_settings, state.AccessTokenResponse, state.ServiceCache);

                // Create Subscription BusinessRule
                var result = await management.CreateSubscriptionBusinessRule(filterUrgency.Value<string>(), filterName.Value<string>(), notificationNameSpace.Value<string>(), postNotificationAPIName.Value<string>());

                // Create ProactiveModel
                var proactiveModel = await _proactiveStateAccessor.GetAsync(context, () => new ProactiveModel()).ConfigureAwait(false);

                proactiveModel[filterName.Value<string>()] = new ProactiveModel.ProactiveData
                {
                    Conversation = context.Activity.GetConversationReference()
                };

                // Get Conversation from Activity
                var conversation = context.Activity.GetConversationReference();

                // ProactiveConversationReferenceMap to save list of conversation
                var proactiveConversationReferenceMap = await _proactiveStateConversationReferenceMapAccessor.GetAsync(
                                        context,
                                        () => new ConversationReferenceMap(),
                                        cancellationToken)
                                    .ConfigureAwait(false);

                // Get Conversations from map
                proactiveConversationReferenceMap.TryGetValue(filterName.Value<string>(), out var listOfConversationReferences);

                if (listOfConversationReferences == null)
                {
                    proactiveConversationReferenceMap[filterName.Value<string>()] = new List<ConversationReference> { conversation };
                }
                else if (!listOfConversationReferences.Contains(conversation))
                {
                    listOfConversationReferences.Add(conversation);
                    proactiveConversationReferenceMap[filterName.Value<string>()] = listOfConversationReferences;
                }

                //ActivityReferenceMap proactiveActivityReferenceMap = await _proactiveStateActivityReferenceMapAccessor.GetAsync(
                //    context,
                //    () => new ActivityReferenceMap(),
                //    cancellationToken)
                //.ConfigureAwait(false);

                //// Store Activity and Thread Id
                //proactiveActivityReferenceMap[context.Activity.Conversation.Id] = new ActivityReference
                //{
                //    ActivityId = context.Activity.Id,
                //    ThreadId = context.Activity.Conversation.Id,
                //};

                // Save ActivityReference and ProactiveState Accessor
                await _proactiveStateConversationReferenceMapAccessor.SetAsync(context, proactiveConversationReferenceMap).ConfigureAwait(false);
                await _proactiveStateAccessor.SetAsync(context, proactiveModel).ConfigureAwait(false);

                // Save Conversation State
                await _proactiveState.SaveChangesAsync(context, false, cancellationToken);
                await _conversationState.SaveChangesAsync(context, false, cancellationToken);

                return new TaskModuleContinueResponse()
                {
                    Value = new TaskModuleTaskInfo()
                    {
                        Title = "ImpactTracker",
                        Height = "medium",
                        Width = 500,
                        Card = new Attachment
                        {
                            ContentType = AdaptiveCard.ContentType,
                            Content = SubscriptionTaskModuleAdaptiveCard.SubscriptionResponseCard($"Created Subscription With Business RuleName: {filterName} in ServiceNow")
                        }
                    }
                };
            }

            return new TaskModuleContinueResponse()
            {
                Value = new TaskModuleTaskInfo()
                {
                    Title = "Subscription",
                    Height = "medium",
                    Width = 500,
                    Card = new Attachment
                    {
                        ContentType = AdaptiveCard.ContentType,
                        Content = SubscriptionTaskModuleAdaptiveCard.SubscriptionResponseCard("Failed To Create Subscription")
                    }
                }
            };
        }
    }
}
