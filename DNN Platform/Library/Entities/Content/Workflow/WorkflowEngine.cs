﻿#region Copyright
// 
// DotNetNuke® - http://www.dotnetnuke.com
// Copyright (c) 2002-2014
// by DotNetNuke Corporation
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated 
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and 
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or substantial portions 
// of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED 
// TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF 
// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DotNetNuke.Common.Utilities;
using DotNetNuke.Entities.Content.Workflow.Exceptions;
using DotNetNuke.Entities.Content.Workflow.Repositories;
using DotNetNuke.Entities.Portals;
using DotNetNuke.Entities.Users;
using DotNetNuke.Framework;
using DotNetNuke.Security.Roles;
using DotNetNuke.Services.Localization;
using DotNetNuke.Services.Social.Notifications;

namespace DotNetNuke.Entities.Content.Workflow
{
    // TODO: add metadata doc
    // TODO: localize exceptions
    public class WorkflowEngine : ServiceLocator<IWorkflowEngine, WorkflowEngine>, IWorkflowEngine
    {
        #region Constants
        private const string ContentWorkflowNotificationType = "ContentWorkflowNotification";
        private const string ContentWorkflowNotificationNoActionType = "ContentWorkflowNoActionNotification";
        #endregion

        #region Members
        private readonly IContentController _contentController;
        private readonly IWorkflowRepository _workflowRepository;
        private readonly IWorkflowStateRepository _workflowStateRepository;
        private readonly IWorkflowStatePermissionsRepository _workflowStatePermissionsRepository;
        private readonly IWorkflowLogRepository _workflowLogRepository;
        private readonly IWorkflowActionRepository _workflowActionRepository;
        private readonly IUserController _userController;
        private readonly IWorkflowSecurity _workflowSecurity;
        private readonly INotificationsController _notificationsController;
        #endregion

        #region Constructor
        public WorkflowEngine()
        {
            _contentController = new ContentController();
            _workflowRepository = WorkflowRepository.Instance;
            _workflowStateRepository = WorkflowStateRepository.Instance;
            _workflowStatePermissionsRepository = WorkflowStatePermissionsRepository.Instance;
            _workflowLogRepository = WorkflowLogRepository.Instance;
            _workflowActionRepository = WorkflowActionRepository.Instance;
            _workflowSecurity = WorkflowSecurity.Instance;
            _userController = UserController.Instance;
            _notificationsController = NotificationsController.Instance;
        }
        #endregion

        #region Private Methods

        private void DiscardWorkflowInternal(ContentItem contentItem, int userId)
        {
            PerformWorkflowAction(contentItem, userId, WorkflowActionTypes.DiscardWorkflow.ToString());
        }

        private void CompleteWorkflowInternal(ContentItem contentItem, int userId)
        {
            PerformWorkflowAction(contentItem, userId, WorkflowActionTypes.CompleteWorkflow.ToString());
        }

        private void PerformWorkflowAction(ContentItem contentItem, int userId, string actionType)
        {
            var action = _workflowActionRepository.GetWorkflowAction(contentItem.ContentTypeId, actionType);
            if (action == null)
            {
                return;
            }

            var workflowAction = Reflection.CreateInstance(Reflection.CreateType(action.ActionSource)) as IWorkflowAction;
            if (workflowAction != null)
            {
                workflowAction.DoAction(contentItem, userId);
            }
        }

        private void UpdateContentItemWorkflowState(int stateId, ContentItem item)
        {
            item.StateID = stateId;
            _contentController.UpdateContentItem(item);
        }

        private UserInfo GetUserThatHaveSubmittedDraftState(ContentWorkflow workflow, int contentItemId)
        {
            var logs = _workflowLogRepository.GetWorkflowLogs(contentItemId, workflow.WorkflowID);

            var logDraftCompleted = logs
                .OrderByDescending(l => l.Date)
                .FirstOrDefault(l => l.Action.Equals(GetWorkflowActionText(ContentWorkflowLogType.DraftCompleted))); // TODO: use a key

            if (logDraftCompleted != null && logDraftCompleted.User != Null.NullInteger)
            {
                return _userController.GetUserById(workflow.PortalID, logDraftCompleted.User);
            }
            return null;
        }

        #region Notification utilities
        private string GetWorkflowNotificationContext(ContentItem contentItem, ContentWorkflowState state)
        {
            return string.Format("{0}:{1}:{2}", contentItem.ContentItemId, state.WorkflowID, state.StateID);
        }

        private void DeleteWorkflowNotifications(ContentItem contentItem, ContentWorkflowState state)
        {
            var context = GetWorkflowNotificationContext(contentItem, state);
            var notificationTypeId = _notificationsController.GetNotificationType(ContentWorkflowNotificationType).NotificationTypeId;
            var notifications = _notificationsController.GetNotificationByContext(notificationTypeId, context);
            foreach (var notification in notifications)
            {
                _notificationsController.DeleteAllNotificationRecipients(notification.NotificationID);
            }
        }

        private void SendNotificationToAuthor(StateTransaction stateTransaction, ContentWorkflow workflow, ContentItem contentItem)
        {
            var user = GetUserThatHaveSubmittedDraftState(workflow, contentItem.ContentItemId);
            if (user == null)
            {
                Services.Exceptions.Exceptions.LogException(new WorkflowException("Author cannot be found. Notification to the author won't be sent"));
                return;
            }

            if (user.UserID == stateTransaction.UserId)
            {
                return;
            }

            var fullbody = AttachComment(stateTransaction.Message.Body, stateTransaction.Message.UserComment);

            var notification = new Notification
            {
                NotificationTypeID = _notificationsController.GetNotificationType(ContentWorkflowNotificationNoActionType).NotificationTypeId,
                Subject = stateTransaction.Message.Subject,
                Body = fullbody,
                IncludeDismissAction = true,
                SenderUserID = stateTransaction.UserId
            };

            _notificationsController.SendNotification(notification, workflow.PortalID, null, new []{ user });
        }

        private void SendNotificationsToReviewers(ContentItem contentItem, ContentWorkflowState state, StateTransactionMessage message, int senderUserId, PortalSettings portalSettings)
        {
            if (!state.SendNotification)
            {
                return;
            }

            var permissions = _workflowStatePermissionsRepository.GetWorkflowStatePermissionByState(state.StateID).ToArray();
            var users = GetUsersFromPermissions(portalSettings, permissions, state.SendNotificationToAdministrators).ToArray();
            var roles = GetRolesFromPermissions(portalSettings, permissions, state.SendNotificationToAdministrators).ToArray();

            roles = roles.ToArray();
            users = users.ToArray();

            if (!roles.Any() && !users.Any())
            {
                return; // If there are no receivers, the notification is avoided
            }

            var fullbody = AttachComment(message.Body, message.UserComment);

            var notification = new Notification
            {
                NotificationTypeID = _notificationsController.GetNotificationType(ContentWorkflowNotificationType).NotificationTypeId,
                Subject = message.Subject,
                Body = fullbody,
                IncludeDismissAction = true,
                SenderUserID = senderUserId,
                Context = GetWorkflowNotificationContext(contentItem, state)
            };

            //TODO: source and params needs to be reviewed
            //append the context
            //if (!string.IsNullOrEmpty(source))
            //{
            //    if (parameters != null && parameters.Length > 0)
            //    {
            //        source = string.Format("{0};{1}", source, string.Join(";", parameters));
            //    }
            //    notification.Context = source;
            //}

            _notificationsController.SendNotification(notification, portalSettings.PortalId, roles.ToList(), users.ToList());
        }

        private static string AttachComment(string body, string userComment)
        {
            if (string.IsNullOrEmpty(body))
            {
                return string.Empty;
            }

            const string commentTag = "[COMMENT]";

            if (!body.Contains(commentTag))
            {
                body += "<br/><br/>" + commentTag;
            }

            return body.Replace(commentTag, userComment);
        } 

        private static IEnumerable<RoleInfo> GetRolesFromPermissions(PortalSettings settings, IEnumerable<ContentWorkflowStatePermission> permissions, bool includeAdministrators)
        {
            var roles = (from permission in permissions 
                         where permission.AllowAccess && permission.RoleID > Null.NullInteger 
                         select RoleController.Instance.GetRoleById(settings.PortalId, permission.RoleID)).ToList();

            if (!includeAdministrators)
            {
                return roles;
            }

            if (IsAdministratorRoleAlreadyIncluded(settings, roles))
            {
                return roles;
            }

            var adminRole = RoleController.Instance.GetRoleByName(settings.PortalId, settings.AdministratorRoleName);
            roles.Add(adminRole);
            return roles;
        }

        private static bool IsAdministratorRoleAlreadyIncluded(PortalSettings settings, IEnumerable<RoleInfo> roles)
        {
            return roles.Any(r => r.RoleName == settings.AdministratorRoleName);
        }

        private static IEnumerable<UserInfo> GetUsersFromPermissions(PortalSettings settings, IEnumerable<ContentWorkflowStatePermission> permissions, bool includeAdministrators)
        {
            var users = (from permission in permissions 
                         where permission.AllowAccess && permission.UserID > Null.NullInteger 
                         select UserController.GetUserById(settings.PortalId, permission.UserID)).ToList();

            return includeAdministrators ? IncludeSuperUsers(users) : users;
        }

        private static IEnumerable<UserInfo> IncludeSuperUsers(ICollection<UserInfo> users)
        {
            var superUsers = UserController.GetUsers(false, true, Null.NullInteger);
            foreach (UserInfo superUser in superUsers)
            {
                if (IsSuperUserNotIncluded(users, superUser))
                {
                    users.Add(superUser);
                }
            }
            return users;
        }

        private static bool IsSuperUserNotIncluded(IEnumerable<UserInfo> users, UserInfo superUser)
        {
            return users.All(u => u.UserID != superUser.UserID);
        }
        #endregion

        #region Log Workflow utilities
        private void AddWorkflowCommentLog(ContentItem contentItem, int userId, string userComment)
        {
            if (string.IsNullOrEmpty(userComment))
            {
                return;
            }
            var state = _workflowStateRepository.GetWorkflowStateByID(contentItem.StateID);
            AddWorkflowLog(contentItem, state, ContentWorkflowLogType.CommentProvided, userId, userComment);
        }
        private void AddWorkflowCommentLog(ContentItem contentItem, ContentWorkflowState state, int userId, string userComment)
        {
            if (string.IsNullOrEmpty(userComment))
            {
                return;
            }
            AddWorkflowLog(contentItem, state, ContentWorkflowLogType.CommentProvided, userId, userComment);
        }

        private void AddWorkflowLog(ContentItem contentItem, ContentWorkflowLogType logType, int userId, string userComment = null)
        {
            var state = _workflowStateRepository.GetWorkflowStateByID(contentItem.StateID);
            AddWorkflowLog(contentItem, state, logType, userId, userComment);
        }

        private void AddWorkflowLog(ContentItem contentItem, ContentWorkflowState state, ContentWorkflowLogType logType, int userId, string userComment = null)
        {
            var workflow = WorkflowManager.Instance.GetWorkflow(contentItem);
            var logTypeText = GetWorkflowActionComment(logType);
            var actionText = GetWorkflowActionText(logType);

            var logComment = ReplaceNotificationTokens(logTypeText, workflow, contentItem, state, userId, userComment);

            _workflowLogRepository.AddWorkflowLog(contentItem.ContentItemId, workflow.WorkflowID, actionText, logComment, userId);
        }

        private static string GetWorkflowActionText(ContentWorkflowLogType logType)
        {
            var logName = Enum.GetName(typeof(ContentWorkflowLogType), logType);
            return Localization.GetString(logName + ".Action");
        }

        private static string GetWorkflowActionComment(ContentWorkflowLogType logType)
        {
            var logName = Enum.GetName(typeof(ContentWorkflowLogType), logType);
            return Localization.GetString(logName + ".Comment");
        }

        private string ReplaceNotificationTokens(string text, ContentWorkflow workflow, ContentItem item, ContentWorkflowState state, int userId, string comment = "")
        {
            var user = _userController.GetUserById(workflow.PortalID, userId);
            var datetime = DateTime.UtcNow;
            var result = text.Replace("[USER]", user != null ? user.DisplayName : "");
            result = result.Replace("[DATE]", datetime.ToString("F", CultureInfo.CurrentCulture));
            result = result.Replace("[STATE]", state != null ? state.StateName : "");
            result = result.Replace("[WORKFLOW]", workflow.WorkflowName);
            result = result.Replace("[CONTENT]", item != null ? item.ContentTitle : "");
            result = result.Replace("[COMMENT]", !String.IsNullOrEmpty(comment) ? comment : "");
            return result;
        }

        private ContentWorkflowState GetNextWorkflowState(ContentWorkflow workflow, int stateId)
        {
            ContentWorkflowState nextState = null;
            var states = workflow.States.OrderBy(s => s.Order);
            int index;

            // locate the current state
            for (index = 0; index < states.Count(); index++)
            {
                if (states.ElementAt(index).StateID == stateId)
                {
                    break;
                }
            }

            index = index + 1;
            if (index < states.Count())
            {
                nextState = states.ElementAt(index);
            }
            return nextState ?? workflow.FirstState;
        }

        private ContentWorkflowState GetPreviousWorkflowState(ContentWorkflow workflow, int stateId)
        {
            ContentWorkflowState previousState = null;
            var states = workflow.States.OrderBy(s => s.Order);
            int index;

            // locate the current state
            for (index = 0; index < states.Count(); index++)
            {
                if (states.ElementAt(index).StateID == stateId)
                {
                    previousState = states.ElementAt(index - 1);
                    break;
                }
            }

            return previousState ?? workflow.LastState;
        }
        #endregion

        #endregion

        #region Public Methods
        public void StartWorkflow(int workflowId, int contentItemId, int userId)
        {
            var contentItem = _contentController.GetContentItem(contentItemId);
            var workflow = WorkflowManager.Instance.GetWorkflow(contentItem);

            //If already exists a started workflow
            if (workflow != null && !IsWorkflowCompleted(contentItem))
            {
                //TODO; Study if is need to throw an exception
                return;
            }

            if (workflow == null || workflow.WorkflowID != workflowId)
            {
                workflow = _workflowRepository.GetWorkflow(workflowId);
            }

            UpdateContentItemWorkflowState(workflow.FirstState.StateID, contentItem);

            // Delete previous logs
            _workflowLogRepository.DeleteWorkflowLogs(contentItemId, workflowId);

            // Add logs
            AddWorkflowLog(contentItem, ContentWorkflowLogType.WorkflowStarted, userId);
            AddWorkflowLog(contentItem, ContentWorkflowLogType.StateInitiated, userId);
        }

        public void CompleteState(StateTransaction stateTransaction)
        {
            var contentItem = _contentController.GetContentItem(stateTransaction.ContentItemId);
            var workflow = WorkflowManager.Instance.GetWorkflow(contentItem);
            if (workflow == null || IsWorkflowCompleted(contentItem))
            {
                return;
            }

            var isFirstState = workflow.FirstState.StateID == contentItem.StateID;

            if (!isFirstState && !_workflowSecurity.HasStateReviewerPermission(workflow.PortalID, stateTransaction.UserId, contentItem.StateID))
            {
                throw new WorkflowSecurityException();
            }

            var currentState = _workflowStateRepository.GetWorkflowStateByID(contentItem.StateID);
            if (currentState.StateID != stateTransaction.CurrentStateId)
            {
                throw new WorkflowException("Current state id does not match with the content item state id"); // TODO: review and localize error message
            }
            
            var nextState = GetNextWorkflowState(workflow, contentItem.StateID);
            UpdateContentItemWorkflowState(nextState.StateID, contentItem);

            // Add logs
            AddWorkflowCommentLog(contentItem, currentState, stateTransaction.UserId, stateTransaction.Message.UserComment);
            AddWorkflowLog(contentItem, currentState,
                currentState.StateID == workflow.FirstState.StateID
                    ? ContentWorkflowLogType.DraftCompleted
                    : ContentWorkflowLogType.StateCompleted, stateTransaction.UserId);
            AddWorkflowLog(contentItem,
                nextState.StateID == workflow.LastState.StateID
                    ? ContentWorkflowLogType.WorkflowApproved
                    : ContentWorkflowLogType.StateInitiated, stateTransaction.UserId);
            
            if (nextState.StateID == workflow.LastState.StateID)
            {
                CompleteWorkflowInternal(contentItem, stateTransaction.UserId);

                // Send to author - workflow has been completed
                SendNotificationToAuthor(stateTransaction, workflow, contentItem);
            }
            else
            {
                // Send to reviewers
                SendNotificationsToReviewers(contentItem, nextState, stateTransaction.Message, stateTransaction.UserId, new PortalSettings(workflow.PortalID));
            }

            DeleteWorkflowNotifications(contentItem, currentState);
        }

        public void DiscardState(StateTransaction stateTransaction)
        {
            var contentItem = _contentController.GetContentItem(stateTransaction.ContentItemId);
            var workflow = WorkflowManager.Instance.GetWorkflow(contentItem);
            if (workflow == null)
            {
                return;
            }

            var isFirstState = workflow.FirstState.StateID == contentItem.StateID;

            if (!isFirstState && !_workflowSecurity.HasStateReviewerPermission(workflow.PortalID, stateTransaction.UserId, contentItem.StateID))
            {
                throw new WorkflowSecurityException();
            }

            var currentState = _workflowStateRepository.GetWorkflowStateByID(contentItem.StateID);
            if (currentState.StateID != stateTransaction.CurrentStateId)
            {
                throw new WorkflowException("Current state id does not match with the content item state id"); // TODO: review and localize error message
            }

            var isLastState = workflow.LastState.StateID == contentItem.StateID;
            if (isLastState)
            {
                throw new WorkflowException("Cannot discard on last workflow state"); // TODO: review and localize error message
            }

            var previousState = GetPreviousWorkflowState(workflow, contentItem.StateID);
            UpdateContentItemWorkflowState(previousState.StateID, contentItem);

            // Add logs
            AddWorkflowCommentLog(contentItem, currentState, stateTransaction.UserId, stateTransaction.Message.UserComment);
            AddWorkflowLog(contentItem, currentState, ContentWorkflowLogType.StateDiscarded, stateTransaction.UserId);
            AddWorkflowLog(contentItem, ContentWorkflowLogType.StateInitiated, stateTransaction.UserId);
            
            if (previousState.StateID == workflow.LastState.StateID)
            {
                DiscardWorkflowInternal(contentItem, stateTransaction.UserId);
            } 
            else if (previousState.StateID == workflow.FirstState.StateID)
            {
                // Send to author - workflow comes back to draft state
                SendNotificationToAuthor(stateTransaction, workflow, contentItem);
            }
            else
            {
                // TODO: replace reviewers notifications
                SendNotificationsToReviewers(contentItem, previousState, stateTransaction.Message, stateTransaction.UserId, new PortalSettings(workflow.PortalID));
            }

            DeleteWorkflowNotifications(contentItem, currentState);
        }
        
        public bool IsWorkflowCompleted(int contentItemId)
        {
            var contentItem = _contentController.GetContentItem(contentItemId);
            return IsWorkflowCompleted(contentItem);
        }

        public bool IsWorkflowCompleted(ContentItem contentItem)
        {
            if (contentItem.StateID == Null.NullInteger)
            {
                return true;
            }

            var workflow = WorkflowManager.Instance.GetWorkflow(contentItem);
            if (workflow == null) return true; // If item has not workflow, then it is considered as completed

            return contentItem.StateID == Null.NullInteger || workflow.LastState.StateID == contentItem.StateID;
        }

        public bool IsWorkflowOnDraft(int contentItemId)
        {
            var contentItem = _contentController.GetContentItem(contentItemId); //Ensure DB values
            return IsWorkflowOnDraft(contentItem);
        }

        public bool IsWorkflowOnDraft(ContentItem contentItem)
        {
            var workflow = WorkflowManager.Instance.GetWorkflow(contentItem);
            if (workflow == null) return false; // If item has not workflow, then it is not on Draft
            return contentItem.StateID == workflow.FirstState.StateID;
        }

        public void DiscardWorkflow(StateTransaction stateTransaction)
        {
            var contentItem = _contentController.GetContentItem(stateTransaction.ContentItemId);

            var currentState = _workflowStateRepository.GetWorkflowStateByID(contentItem.StateID);
            if (currentState.StateID != stateTransaction.CurrentStateId)
            {
                throw new WorkflowException("Current state id does not match with the content item state id"); // TODO: review and localize error message
            }
            
            var workflow =WorkflowManager.Instance.GetWorkflow(contentItem);
            UpdateContentItemWorkflowState(workflow.LastState.StateID, contentItem);

            // Logs
            AddWorkflowCommentLog(contentItem, stateTransaction.UserId, stateTransaction.Message.UserComment);
            AddWorkflowLog(contentItem, ContentWorkflowLogType.WorkflowDiscarded, stateTransaction.UserId);

            DiscardWorkflowInternal(contentItem, stateTransaction.UserId);
            DeleteWorkflowNotifications(contentItem, currentState);
        }

        public void CompleteWorkflow(StateTransaction stateTransaction)
        {
            var contentItem = _contentController.GetContentItem(stateTransaction.ContentItemId);

            var currentState = _workflowStateRepository.GetWorkflowStateByID(contentItem.StateID);
            if (currentState.StateID != stateTransaction.CurrentStateId)
            {
                throw new WorkflowException("Current state id does not match with the content item state id"); // TODO: review and localize error message
            }
            
            var workflow = WorkflowManager.Instance.GetWorkflow(contentItem);
            UpdateContentItemWorkflowState(workflow.LastState.StateID, contentItem);

            // Logs
            AddWorkflowCommentLog(contentItem, stateTransaction.UserId, stateTransaction.Message.UserComment);
            AddWorkflowLog(contentItem, ContentWorkflowLogType.WorkflowApproved, stateTransaction.UserId);

            CompleteWorkflowInternal(contentItem, stateTransaction.UserId);
            DeleteWorkflowNotifications(contentItem, currentState);
        }
        #endregion

        protected override Func<IWorkflowEngine> GetFactory()
        {
            return () => new WorkflowEngine();
        }
    }
}
