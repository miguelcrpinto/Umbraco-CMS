﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Mail;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Mvc;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration;
using Umbraco.Core.IO;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.Editors;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Persistence;
using Umbraco.Core.Services;
using Umbraco.Core.Strings;
using Umbraco.Web.Editors.Filters;
using Umbraco.Web.Models;
using Umbraco.Web.Models.ContentEditing;
using Umbraco.Web.Models.Identity;
using Umbraco.Web.Mvc;
using Umbraco.Web.WebApi;
using Umbraco.Web.WebApi.Filters;
using Constants = Umbraco.Core.Constants;
using IUser = Umbraco.Core.Models.Membership.IUser;
using Task = System.Threading.Tasks.Task;
using Umbraco.Core.Mapping;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.Hosting;
using Umbraco.Web.Routing;
using Umbraco.Core.Media;
using Umbraco.Web.Security;

namespace Umbraco.Web.Editors
{
    [PluginController("UmbracoApi")]
    [UmbracoApplicationAuthorize(Constants.Applications.Users)]
    [PrefixlessBodyModelValidator]
    [IsCurrentUserModelFilter]
    public class UsersController : UmbracoAuthorizedJsonController
    {
        private readonly IMediaFileSystem _mediaFileSystem;
        private readonly IContentSettings _contentSettings;
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly ISqlContext _sqlContext;
        private readonly IImageUrlGenerator _imageUrlGenerator;
        private readonly ISecuritySettings _securitySettings;
        private readonly IEmailSender _emailSender;

        public UsersController(
            IGlobalSettings globalSettings,
            IUmbracoContextAccessor umbracoContextAccessor,
            ISqlContext sqlContext,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger logger,
            IRuntimeState runtimeState,
            IMediaFileSystem mediaFileSystem,
            IShortStringHelper shortStringHelper,
            UmbracoMapper umbracoMapper,
            IContentSettings contentSettings,
            IHostingEnvironment hostingEnvironment,
            IImageUrlGenerator imageUrlGenerator,
            IPublishedUrlProvider publishedUrlProvider,
            ISecuritySettings securitySettings,
            IEmailSender emailSender)
            : base(globalSettings, umbracoContextAccessor, sqlContext, services, appCaches, logger, runtimeState, shortStringHelper, umbracoMapper, publishedUrlProvider)
        {
            _mediaFileSystem = mediaFileSystem;
            _contentSettings = contentSettings ?? throw new ArgumentNullException(nameof(contentSettings));
            _hostingEnvironment = hostingEnvironment;
            _sqlContext = sqlContext;
            _imageUrlGenerator = imageUrlGenerator;
            _securitySettings = securitySettings;
            _emailSender = emailSender;
        }

        /// <summary>
        /// Returns a list of the sizes of gravatar urls for the user or null if the gravatar server cannot be reached
        /// </summary>
        /// <returns></returns>
        public string[] GetCurrentUserAvatarUrls()
        {
            var urls = UmbracoContext.Security.CurrentUser.GetUserAvatarUrls(AppCaches.RuntimeCache, _mediaFileSystem, _imageUrlGenerator);
            if (urls == null)
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, "Could not access Gravatar endpoint"));

            return urls;
        }

        [AppendUserModifiedHeader("id")]
        [FileUploadCleanupFilter(false)]
        [AdminUsersAuthorize]
        public async Task<HttpResponseMessage> PostSetAvatar(int id)
        {
            return await PostSetAvatarInternal(Request, Services.UserService, AppCaches.RuntimeCache, _mediaFileSystem, ShortStringHelper, _contentSettings, _hostingEnvironment, _imageUrlGenerator, id);
        }

        internal static async Task<HttpResponseMessage> PostSetAvatarInternal(HttpRequestMessage request, IUserService userService, IAppCache cache, IMediaFileSystem mediaFileSystem, IShortStringHelper shortStringHelper, IContentSettings contentSettings, IHostingEnvironment hostingEnvironment, IImageUrlGenerator imageUrlGenerator, int id)
        {
            if (request.Content.IsMimeMultipartContent() == false)
            {
                throw new HttpResponseException(HttpStatusCode.UnsupportedMediaType);
            }

            var root = hostingEnvironment.MapPath(Constants.SystemDirectories.TempFileUploads);
            //ensure it exists
            Directory.CreateDirectory(root);
            var provider = new MultipartFormDataStreamProvider(root);

            var result = await request.Content.ReadAsMultipartAsync(provider);

            //must have a file
            if (result.FileData.Count == 0)
            {
                return request.CreateResponse(HttpStatusCode.NotFound);
            }

            var user = userService.GetUserById(id);
            if (user == null)
                return request.CreateResponse(HttpStatusCode.NotFound);

            var tempFiles = new PostedFiles();

            if (result.FileData.Count > 1)
                return request.CreateValidationErrorResponse("The request was not formatted correctly, only one file can be attached to the request");

            //get the file info
            var file = result.FileData[0];
            var fileName = file.Headers.ContentDisposition.FileName.Trim(new[] { '\"' }).TrimEnd();
            var safeFileName = fileName.ToSafeFileName(shortStringHelper);
            var ext = safeFileName.Substring(safeFileName.LastIndexOf('.') + 1).ToLower();

            if (contentSettings.DisallowedUploadFiles.Contains(ext) == false)
            {
                //generate a path of known data, we don't want this path to be guessable
                user.Avatar = "UserAvatars/" + (user.Id + safeFileName).GenerateHash<SHA1>() + "." + ext;

                using (var fs = System.IO.File.OpenRead(file.LocalFileName))
                {
                    mediaFileSystem.AddFile(user.Avatar, fs, true);
                }

                userService.Save(user);

                //track the temp file so the cleanup filter removes it
                tempFiles.UploadedFiles.Add(new ContentPropertyFile
                {
                    TempFilePath = file.LocalFileName
                });
            }

            return request.CreateResponse(HttpStatusCode.OK, user.GetUserAvatarUrls(cache, mediaFileSystem, imageUrlGenerator));
        }

        [AppendUserModifiedHeader("id")]
        [AdminUsersAuthorize]
        public HttpResponseMessage PostClearAvatar(int id)
        {
            var found = Services.UserService.GetUserById(id);
            if (found == null)
                return Request.CreateResponse(HttpStatusCode.NotFound);

            var filePath = found.Avatar;

            //if the filePath is already null it will mean that the user doesn't have a custom avatar and their gravatar is currently
            //being used (if they have one). This means they want to remove their gravatar too which we can do by setting a special value
            //for the avatar.
            if (filePath.IsNullOrWhiteSpace() == false)
            {
                found.Avatar = null;
            }
            else
            {
                //set a special value to indicate to not have any avatar
                found.Avatar = "none";
            }

            Services.UserService.Save(found);

            if (filePath.IsNullOrWhiteSpace() == false)
            {
                if (_mediaFileSystem.FileExists(filePath))
                    _mediaFileSystem.DeleteFile(filePath);
            }

            return Request.CreateResponse(HttpStatusCode.OK, found.GetUserAvatarUrls(AppCaches.RuntimeCache, _mediaFileSystem, _imageUrlGenerator));
        }

        /// <summary>
        /// Gets a user by Id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [OutgoingEditorModelEvent]
        [AdminUsersAuthorize]
        public UserDisplay GetById(int id)
        {
            var user = Services.UserService.GetUserById(id);
            if (user == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }
            var result = Mapper.Map<IUser, UserDisplay>(user);
            return result;
        }

        /// <summary>
        /// Returns a paged users collection
        /// </summary>
        /// <param name="pageNumber"></param>
        /// <param name="pageSize"></param>
        /// <param name="orderBy"></param>
        /// <param name="orderDirection"></param>
        /// <param name="userGroups"></param>
        /// <param name="userStates"></param>
        /// <param name="filter"></param>
        /// <returns></returns>
        public PagedUserResult GetPagedUsers(
            int pageNumber = 1,
            int pageSize = 10,
            string orderBy = "username",
            Direction orderDirection = Direction.Ascending,
            [FromUri]string[] userGroups = null,
            [FromUri]UserState[] userStates = null,
            string filter = "")
        {
            //following the same principle we had in previous versions, we would only show admins to admins, see
            // https://github.com/umbraco/Umbraco-CMS/blob/dev-v7/src/Umbraco.Web/umbraco.presentation/umbraco/Trees/loadUsers.cs#L91
            // so to do that here, we'll need to check if this current user is an admin and if not we should exclude all user who are
            // also admins

            var hideDisabledUsers = _securitySettings.HideDisabledUsersInBackoffice;
            var excludeUserGroups = new string[0];
            var isAdmin = Security.CurrentUser.IsAdmin();
            if (isAdmin == false)
            {
                //this user is not an admin so in that case we need to exclude all admin users
                excludeUserGroups = new[] {Constants.Security.AdminGroupAlias};
            }

            var filterQuery = _sqlContext.Query<IUser>();

            if (!Security.CurrentUser.IsSuper())
            {
                // only super can see super - but don't use IsSuper, cannot be mapped to SQL
                //filterQuery.Where(x => !x.IsSuper());
                filterQuery.Where(x => x.Id != Constants.Security.SuperUserId);
            }

            if (filter.IsNullOrWhiteSpace() == false)
            {
                filterQuery.Where(x => x.Name.Contains(filter) || x.Username.Contains(filter));
            }

            if (hideDisabledUsers)
            {
                if (userStates == null || userStates.Any() == false)
                {
                    userStates = new[] { UserState.Active, UserState.Invited, UserState.LockedOut, UserState.Inactive };
                }
            }

            long pageIndex = pageNumber - 1;
            long total;
            var result = Services.UserService.GetAll(pageIndex, pageSize, out total, orderBy, orderDirection, userStates, userGroups, excludeUserGroups, filterQuery);

            var paged = new PagedUserResult(total, pageNumber, pageSize)
            {
                Items = Mapper.MapEnumerable<IUser, UserBasic>(result),
                UserStates = Services.UserService.GetUserStates()
            };

            return paged;
        }

        /// <summary>
        /// Creates a new user
        /// </summary>
        /// <param name="userSave"></param>
        /// <returns></returns>
        public async Task<UserDisplay> PostCreateUser(UserInvite userSave)
        {
            if (userSave == null) throw new ArgumentNullException("userSave");

            if (ModelState.IsValid == false)
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));
            }

            if (_securitySettings.UsernameIsEmail)
            {
                //ensure they are the same if we're using it
                userSave.Username = userSave.Email;
            }
            else
            {
                //first validate the username if were showing it
                CheckUniqueUsername(userSave.Username, null);
            }
            CheckUniqueEmail(userSave.Email, null);

            //Perform authorization here to see if the current user can actually save this user with the info being requested
            var authHelper = new UserEditorAuthorizationHelper(Services.ContentService, Services.MediaService, Services.UserService, Services.EntityService);
            var canSaveUser = authHelper.IsAuthorized(Security.CurrentUser, null, null, null, userSave.UserGroups);
            if (canSaveUser == false)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.Unauthorized, canSaveUser.Result));
            }

            //we want to create the user with the UserManager, this ensures the 'empty' (special) password
            //format is applied without us having to duplicate that logic
            var identityUser = BackOfficeIdentityUser.CreateNew(GlobalSettings, userSave.Username, userSave.Email, GlobalSettings.DefaultUILanguage);
            identityUser.Name = userSave.Name;

            var created = await UserManager.CreateAsync(identityUser);
            if (created.Succeeded == false)
            {
                throw new HttpResponseException(
                    Request.CreateNotificationValidationErrorResponse(created.Errors.ToErrorMessage()));
            }

            string resetPassword;
            var password = UserManager.GeneratePassword();

            var result = await UserManager.AddPasswordAsync(identityUser, password);
            if (result.Succeeded == false)
            {
                throw new HttpResponseException(
                    Request.CreateNotificationValidationErrorResponse(created.Errors.ToErrorMessage()));
            }

            resetPassword = password;

            //now re-look the user back up which will now exist
            var user = Services.UserService.GetByEmail(userSave.Email);

            //map the save info over onto the user
            user = Mapper.Map(userSave, user);

            //since the back office user is creating this user, they will be set to approved
            user.IsApproved = true;

            Services.UserService.Save(user);

            var display = Mapper.Map<UserDisplay>(user);
            display.ResetPasswordValue = resetPassword;
            return display;
        }

        /// <summary>
        /// Invites a user
        /// </summary>
        /// <param name="userSave"></param>
        /// <returns></returns>
        /// <remarks>
        /// This will email the user an invite and generate a token that will be validated in the email
        /// </remarks>
        public async Task<UserDisplay> PostInviteUser(UserInvite userSave)
        {
            if (userSave == null) throw new ArgumentNullException("userSave");

            if (userSave.Message.IsNullOrWhiteSpace())
                ModelState.AddModelError("Message", "Message cannot be empty");

            if (ModelState.IsValid == false)
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));
            }

            if (EmailSender.CanSendRequiredEmail(GlobalSettings) == false)
            {
                throw new HttpResponseException(
                    Request.CreateNotificationValidationErrorResponse("No Email server is configured"));
            }

            IUser user;
            if (_securitySettings.UsernameIsEmail)
            {
                //ensure it's the same
                userSave.Username = userSave.Email;
            }
            else
            {
                //first validate the username if we're showing it
                user = CheckUniqueUsername(userSave.Username, u => u.LastLoginDate != default(DateTime) || u.EmailConfirmedDate.HasValue);
            }
            user = CheckUniqueEmail(userSave.Email, u => u.LastLoginDate != default(DateTime) || u.EmailConfirmedDate.HasValue);

            //Perform authorization here to see if the current user can actually save this user with the info being requested
            var authHelper = new UserEditorAuthorizationHelper(Services.ContentService, Services.MediaService, Services.UserService, Services.EntityService);
            var canSaveUser = authHelper.IsAuthorized(Security.CurrentUser, user, null, null, userSave.UserGroups);
            if (canSaveUser == false)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.Unauthorized, canSaveUser.Result));
            }

            if (user == null)
            {
                //we want to create the user with the UserManager, this ensures the 'empty' (special) password
                //format is applied without us having to duplicate that logic
                var identityUser = BackOfficeIdentityUser.CreateNew(GlobalSettings, userSave.Username, userSave.Email, GlobalSettings.DefaultUILanguage);
                identityUser.Name = userSave.Name;

                var created = await UserManager.CreateAsync(identityUser);
                if (created.Succeeded == false)
                {
                    throw new HttpResponseException(
                        Request.CreateNotificationValidationErrorResponse(created.Errors.ToErrorMessage()));
                }

                //now re-look the user back up
                user = Services.UserService.GetByEmail(userSave.Email);
            }

            //map the save info over onto the user
            user = Mapper.Map(userSave, user);

            //ensure the invited date is set
            user.InvitedDate = DateTime.Now;

            //Save the updated user
            Services.UserService.Save(user);
            var display = Mapper.Map<UserDisplay>(user);

            //send the email

            await SendUserInviteEmailAsync(display, Security.CurrentUser.Name, Security.CurrentUser.Email, user, userSave.Message);

            display.AddSuccessNotification(Services.TextService.Localize("speechBubbles/resendInviteHeader"), Services.TextService.Localize("speechBubbles/resendInviteSuccess", new[] { user.Name }));

            return display;
        }

        private IUser CheckUniqueEmail(string email, Func<IUser, bool> extraCheck)
        {
            var user = Services.UserService.GetByEmail(email);
            if (user != null && (extraCheck == null || extraCheck(user)))
            {
                ModelState.AddModelError("Email", "A user with the email already exists");
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));
            }
            return user;
        }

        private IUser CheckUniqueUsername(string username, Func<IUser, bool> extraCheck)
        {
            var user = Services.UserService.GetByUsername(username);
            if (user != null && (extraCheck == null || extraCheck(user)))
            {
                ModelState.AddModelError(
                    _securitySettings.UsernameIsEmail ? "Email" : "Username",
                    "A user with the username already exists");
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));
            }
            return user;
        }

        private HttpContextBase EnsureHttpContext()
        {
            var attempt = this.TryGetHttpContext();
            if (attempt.Success == false)
                throw new InvalidOperationException("This method requires that an HttpContext be active");
            return attempt.Result;
        }

        private async Task SendUserInviteEmailAsync(UserBasic userDisplay, string from, string fromEmail, IUser to, string message)
        {
            var user = await UserManager.FindByIdAsync(((int) userDisplay.Id).ToString());
            var token = await UserManager.GenerateEmailConfirmationTokenAsync(user);

            var inviteToken = string.Format("{0}{1}{2}",
                (int)userDisplay.Id,
                WebUtility.UrlEncode("|"),
                token.ToUrlBase64());

            // Get an mvc helper to get the url
            var http = EnsureHttpContext();
            var urlHelper = new UrlHelper(http.Request.RequestContext);
            var action = urlHelper.Action("VerifyInvite", "BackOffice",
                new
                {
                    area = GlobalSettings.GetUmbracoMvcArea(_hostingEnvironment),
                    invite = inviteToken
                });

            // Construct full URL using configured application URL (which will fall back to request)
            var applicationUri = RuntimeState.ApplicationUrl;
            var inviteUri = new Uri(applicationUri, action);

            var emailSubject = Services.TextService.Localize("user/inviteEmailCopySubject",
                //Ensure the culture of the found user is used for the email!
                UmbracoUserExtensions.GetUserCulture(to.Language, Services.TextService, GlobalSettings));
            var emailBody = Services.TextService.Localize("user/inviteEmailCopyFormat",
                //Ensure the culture of the found user is used for the email!
                UmbracoUserExtensions.GetUserCulture(to.Language, Services.TextService, GlobalSettings),
                new[] { userDisplay.Name, from, message, inviteUri.ToString(), fromEmail });

            var mailMessage = new MailMessage()
            {
                Subject = emailSubject,
                Body = emailBody,
                IsBodyHtml = true,
                To = { to.Email}
            };

            await _emailSender.SendAsync(mailMessage);
        }

        /// <summary>
        /// Saves a user
        /// </summary>
        /// <param name="userSave"></param>
        /// <returns></returns>
        [OutgoingEditorModelEvent]
        public async Task<UserDisplay> PostSaveUser(UserSave userSave)
        {
            if (userSave == null) throw new ArgumentNullException("userSave");

            if (ModelState.IsValid == false)
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));
            }

            var intId = userSave.Id.TryConvertTo<int>();
            if (intId.Success == false)
                throw new HttpResponseException(HttpStatusCode.NotFound);

            var found = Services.UserService.GetUserById(intId.Result);
            if (found == null)
                throw new HttpResponseException(HttpStatusCode.NotFound);

            //Perform authorization here to see if the current user can actually save this user with the info being requested
            var authHelper = new UserEditorAuthorizationHelper(Services.ContentService, Services.MediaService, Services.UserService, Services.EntityService);
            var canSaveUser = authHelper.IsAuthorized(Security.CurrentUser, found, userSave.StartContentIds, userSave.StartMediaIds, userSave.UserGroups);
            if (canSaveUser == false)
            {
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.Unauthorized, canSaveUser.Result));
            }

            var hasErrors = false;

            var existing = Services.UserService.GetByEmail(userSave.Email);
            if (existing != null && existing.Id != userSave.Id)
            {
                ModelState.AddModelError("Email", "A user with the email already exists");
                hasErrors = true;
            }
            existing = Services.UserService.GetByUsername(userSave.Username);
            if (existing != null && existing.Id != userSave.Id)
            {
                ModelState.AddModelError("Username", "A user with the username already exists");
                hasErrors = true;
            }
            // going forward we prefer to align usernames with email, so we should cross-check to make sure
            // the email or username isn't somehow being used by anyone.
            existing = Services.UserService.GetByEmail(userSave.Username);
            if (existing != null && existing.Id != userSave.Id)
            {
                ModelState.AddModelError("Username", "A user using this as their email already exists");
                hasErrors = true;
            }
            existing = Services.UserService.GetByUsername(userSave.Email);
            if (existing != null && existing.Id != userSave.Id)
            {
                ModelState.AddModelError("Email", "A user using this as their username already exists");
                hasErrors = true;
            }

            // if the found user has their email for username, we want to keep this synced when changing the email.
            // we have already cross-checked above that the email isn't colliding with anything, so we can safely assign it here.
            if (_securitySettings.UsernameIsEmail && found.Username == found.Email && userSave.Username != userSave.Email)
            {
                userSave.Username = userSave.Email;
            }

            if (hasErrors)
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));

            //merge the save data onto the user
            var user = Mapper.Map(userSave, found);

            Services.UserService.Save(user);

            var display = Mapper.Map<UserDisplay>(user);

            display.AddSuccessNotification(Services.TextService.Localize("speechBubbles/operationSavedHeader"), Services.TextService.Localize("speechBubbles/editUserSaved"));
            return display;
        }

        /// <summary>
        ///
        /// </summary>
        /// <param name="changingPasswordModel"></param>
        /// <returns></returns>
        public async Task<ModelWithNotifications<string>> PostChangePassword(ChangingPasswordModel changingPasswordModel)
        {
            changingPasswordModel = changingPasswordModel ?? throw new ArgumentNullException(nameof(changingPasswordModel));

            if (ModelState.IsValid == false)
            {
                throw new HttpResponseException(Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState));
            }

            var intId = changingPasswordModel.Id.TryConvertTo<int>();
            if (intId.Success == false)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var found = Services.UserService.GetUserById(intId.Result);
            if (found == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var passwordChanger = new PasswordChanger(Logger);
            var passwordChangeResult = await passwordChanger.ChangePasswordWithIdentityAsync(Security.CurrentUser, found, changingPasswordModel, UserManager);

            if (passwordChangeResult.Success)
            {
                var result = new ModelWithNotifications<string>(passwordChangeResult.Result.ResetPassword);
                result.AddSuccessNotification(Services.TextService.Localize("general/success"), Services.TextService.Localize("user/passwordChangedGeneric"));
                return result;
            }

            foreach (var memberName in passwordChangeResult.Result.ChangeError.MemberNames)
            {
                ModelState.AddModelError(memberName, passwordChangeResult.Result.ChangeError.ErrorMessage);
            }

            throw new HttpResponseException(Request.CreateValidationErrorResponse(ModelState));
        }


        /// <summary>
        /// Disables the users with the given user ids
        /// </summary>
        /// <param name="userIds"></param>
        [AdminUsersAuthorize("userIds")]
        public HttpResponseMessage PostDisableUsers([FromUri]int[] userIds)
        {
            var tryGetCurrentUserId = Security.GetUserId();
            if (tryGetCurrentUserId && userIds.Contains(tryGetCurrentUserId.Result))
            {
                throw new HttpResponseException(
                    Request.CreateNotificationValidationErrorResponse("The current user cannot disable itself"));
            }

            var users = Services.UserService.GetUsersById(userIds).ToArray();
            foreach (var u in users)
            {
                u.IsApproved = false;
                u.InvitedDate = null;
            }
            Services.UserService.Save(users);

            if (users.Length > 1)
            {
                return Request.CreateNotificationSuccessResponse(
                    Services.TextService.Localize("speechBubbles/disableUsersSuccess", new[] {userIds.Length.ToString()}));
            }

            return Request.CreateNotificationSuccessResponse(
                Services.TextService.Localize("speechBubbles/disableUserSuccess", new[] { users[0].Name }));
        }

        /// <summary>
        /// Enables the users with the given user ids
        /// </summary>
        /// <param name="userIds"></param>
        [AdminUsersAuthorize("userIds")]
        public HttpResponseMessage PostEnableUsers([FromUri]int[] userIds)
        {
            var users = Services.UserService.GetUsersById(userIds).ToArray();
            foreach (var u in users)
            {
                u.IsApproved = true;
            }
            Services.UserService.Save(users);

            if (users.Length > 1)
            {
                return Request.CreateNotificationSuccessResponse(
                    Services.TextService.Localize("speechBubbles/enableUsersSuccess", new[] { userIds.Length.ToString() }));
            }

            return Request.CreateNotificationSuccessResponse(
                Services.TextService.Localize("speechBubbles/enableUserSuccess", new[] { users[0].Name }));
        }

        /// <summary>
        /// Unlocks the users with the given user ids
        /// </summary>
        /// <param name="userIds"></param>
        [AdminUsersAuthorize("userIds")]
        public async Task<HttpResponseMessage> PostUnlockUsers([FromUri]int[] userIds)
        {
            if (userIds.Length <= 0) return Request.CreateResponse(HttpStatusCode.OK);

            foreach (var u in userIds)
            {
                var user = await UserManager.FindByIdAsync(u.ToString());
                if (user == null) throw new InvalidOperationException();

                var unlockResult = await UserManager.SetLockoutEndDateAsync(user, DateTimeOffset.Now);
                if (unlockResult.Succeeded == false)
                {
                    return Request.CreateValidationErrorResponse(
                        string.Format("Could not unlock for user {0} - error {1}", u, unlockResult.Errors.ToErrorMessage()));
                }

                if (userIds.Length == 1)
                {
                    return Request.CreateNotificationSuccessResponse(
                        Services.TextService.Localize("speechBubbles/unlockUserSuccess", new[] {user.Name}));
                }
            }

            return Request.CreateNotificationSuccessResponse(
                Services.TextService.Localize("speechBubbles/unlockUsersSuccess", new[] {userIds.Length.ToString()}));
        }

        [AdminUsersAuthorize("userIds")]
        public HttpResponseMessage PostSetUserGroupsOnUsers([FromUri]string[] userGroupAliases, [FromUri]int[] userIds)
        {
            var users = Services.UserService.GetUsersById(userIds).ToArray();
            var userGroups = Services.UserService.GetUserGroupsByAlias(userGroupAliases).Select(x => x.ToReadOnlyGroup()).ToArray();
            foreach (var u in users)
            {
                u.ClearGroups();
                foreach (var userGroup in userGroups)
                {
                    u.AddGroup(userGroup);
                }
            }
            Services.UserService.Save(users);
            return Request.CreateNotificationSuccessResponse(
                Services.TextService.Localize("speechBubbles/setUserGroupOnUsersSuccess"));
        }

        /// <summary>
        /// Deletes the non-logged in user provided id
        /// </summary>
        /// <param name="id">User Id</param>
        /// <remarks>
        /// Limited to users that haven't logged in to avoid issues with related records constrained
        /// with a foreign key on the user Id
        /// </remarks>
        [AdminUsersAuthorize]
        public HttpResponseMessage PostDeleteNonLoggedInUser(int id)
        {
            var user = Services.UserService.GetUserById(id);
            if (user == null)
            {
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            // Check user hasn't logged in.  If they have they may have made content changes which will mean
            // the Id is associated with audit trails, versions etc. and can't be removed.
            if (user.LastLoginDate != default(DateTime))
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }

            var userName = user.Name;
            Services.UserService.Delete(user, true);

            return Request.CreateNotificationSuccessResponse(
                Services.TextService.Localize("speechBubbles/deleteUserSuccess", new[] { userName }));
        }

        public class PagedUserResult : PagedResult<UserBasic>
        {
            public PagedUserResult(long totalItems, long pageNumber, long pageSize) : base(totalItems, pageNumber, pageSize)
            {
                UserStates = new Dictionary<UserState, int>();
            }

            /// <summary>
            /// This is basically facets of UserStates key = state, value = count
            /// </summary>
            [DataMember(Name = "userStates")]
            public IDictionary<UserState, int> UserStates { get; set; }
        }

    }
}
