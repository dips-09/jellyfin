﻿#pragma warning disable CA1801

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// The session controller.
    /// </summary>
    [Route("")]
    public class SessionController : BaseJellyfinApiController
    {
        private readonly ISessionManager _sessionManager;
        private readonly IUserManager _userManager;
        private readonly IAuthorizationContext _authContext;
        private readonly IDeviceManager _deviceManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="SessionController"/> class.
        /// </summary>
        /// <param name="sessionManager">Instance of <see cref="ISessionManager"/> interface.</param>
        /// <param name="userManager">Instance of <see cref="IUserManager"/> interface.</param>
        /// <param name="authContext">Instance of <see cref="IAuthorizationContext"/> interface.</param>
        /// <param name="deviceManager">Instance of <see cref="IDeviceManager"/> interface.</param>
        public SessionController(
            ISessionManager sessionManager,
            IUserManager userManager,
            IAuthorizationContext authContext,
            IDeviceManager deviceManager)
        {
            _sessionManager = sessionManager;
            _userManager = userManager;
            _authContext = authContext;
            _deviceManager = deviceManager;
        }

        /// <summary>
        /// Gets a list of sessions.
        /// </summary>
        /// <param name="controllableByUserId">Filter by sessions that a given user is allowed to remote control.</param>
        /// <param name="deviceId">Filter by device Id.</param>
        /// <param name="activeWithinSeconds">Optional. Filter by sessions that were active in the last n seconds.</param>
        /// <response code="200">List of sessions returned.</response>
        /// <returns>An <see cref="IEnumerable{SessionInfo}"/> with the available sessions.</returns>
        [HttpGet("Sessions")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<SessionInfo>> GetSessions(
            [FromQuery] Guid? controllableByUserId,
            [FromQuery] string? deviceId,
            [FromQuery] int? activeWithinSeconds)
        {
            var result = _sessionManager.Sessions;

            if (!string.IsNullOrEmpty(deviceId))
            {
                result = result.Where(i => string.Equals(i.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            }

            if (controllableByUserId.HasValue && !controllableByUserId.Equals(Guid.Empty))
            {
                result = result.Where(i => i.SupportsRemoteControl);

                var user = _userManager.GetUserById(controllableByUserId.Value);

                if (!user.HasPermission(PermissionKind.EnableRemoteControlOfOtherUsers))
                {
                    result = result.Where(i => i.UserId.Equals(Guid.Empty) || i.ContainsUser(controllableByUserId.Value));
                }

                if (!user.HasPermission(PermissionKind.EnableSharedDeviceControl))
                {
                    result = result.Where(i => !i.UserId.Equals(Guid.Empty));
                }

                if (activeWithinSeconds.HasValue && activeWithinSeconds.Value > 0)
                {
                    var minActiveDate = DateTime.UtcNow.AddSeconds(0 - activeWithinSeconds.Value);
                    result = result.Where(i => i.LastActivityDate >= minActiveDate);
                }

                result = result.Where(i =>
                {
                    if (!string.IsNullOrWhiteSpace(i.DeviceId))
                    {
                        if (!_deviceManager.CanAccessDevice(user, i.DeviceId))
                        {
                            return false;
                        }
                    }

                    return true;
                });
            }

            return Ok(result);
        }

        /// <summary>
        /// Instructs a session to browse to an item or view.
        /// </summary>
        /// <param name="sessionId">The session Id.</param>
        /// <param name="itemType">The type of item to browse to.</param>
        /// <param name="itemId">The Id of the item.</param>
        /// <param name="itemName">The name of the item.</param>
        /// <response code="204">Instruction sent to session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/Viewing")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult DisplayContent(
            [FromRoute] string? sessionId,
            [FromQuery] string? itemType,
            [FromQuery] string? itemId,
            [FromQuery] string? itemName)
        {
            var command = new BrowseRequest
            {
                ItemId = itemId,
                ItemName = itemName,
                ItemType = itemType
            };

            _sessionManager.SendBrowseCommand(
                RequestHelpers.GetSession(_sessionManager, _authContext, Request).Id,
                sessionId,
                command,
                CancellationToken.None);

            return NoContent();
        }

        /// <summary>
        /// Instructs a session to play an item.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="itemIds">The ids of the items to play, comma delimited.</param>
        /// <param name="startPositionTicks">The starting position of the first item.</param>
        /// <param name="playCommand">The type of play command to issue (PlayNow, PlayNext, PlayLast). Clients who have not yet implemented play next and play last may play now.</param>
        /// <param name="playRequest">The <see cref="PlayRequest"/>.</param>
        /// <response code="204">Instruction sent to session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/Playing")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult Play(
            [FromRoute] string? sessionId,
            [FromQuery] Guid[] itemIds,
            [FromQuery] long? startPositionTicks,
            [FromQuery] PlayCommand playCommand,
            [FromBody, Required] PlayRequest playRequest)
        {
            if (playRequest == null)
            {
                throw new ArgumentException("Request Body may not be null");
            }

            playRequest.ItemIds = itemIds;
            playRequest.StartPositionTicks = startPositionTicks;
            playRequest.PlayCommand = playCommand;

            _sessionManager.SendPlayCommand(
                RequestHelpers.GetSession(_sessionManager, _authContext, Request).Id,
                sessionId,
                playRequest,
                CancellationToken.None);

            return NoContent();
        }

        /// <summary>
        /// Issues a playstate command to a client.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="playstateRequest">The <see cref="PlaystateRequest"/>.</param>
        /// <response code="204">Playstate command sent to session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/Playing/{command}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult SendPlaystateCommand(
            [FromRoute] string? sessionId,
            [FromBody] PlaystateRequest playstateRequest)
        {
            _sessionManager.SendPlaystateCommand(
                RequestHelpers.GetSession(_sessionManager, _authContext, Request).Id,
                sessionId,
                playstateRequest,
                CancellationToken.None);

            return NoContent();
        }

        /// <summary>
        /// Issues a system command to a client.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="command">The command to send.</param>
        /// <response code="204">System command sent to session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/System/{command}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult SendSystemCommand(
            [FromRoute] string? sessionId,
            [FromRoute] string? command)
        {
            var name = command;
            if (Enum.TryParse(name, true, out GeneralCommandType commandType))
            {
                name = commandType.ToString();
            }

            var currentSession = RequestHelpers.GetSession(_sessionManager, _authContext, Request);
            var generalCommand = new GeneralCommand
            {
                Name = name,
                ControllingUserId = currentSession.UserId
            };

            _sessionManager.SendGeneralCommand(currentSession.Id, sessionId, generalCommand, CancellationToken.None);

            return NoContent();
        }

        /// <summary>
        /// Issues a general command to a client.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="command">The command to send.</param>
        /// <response code="204">General command sent to session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/Command/{command}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult SendGeneralCommand(
            [FromRoute] string? sessionId,
            [FromRoute] string? command)
        {
            var currentSession = RequestHelpers.GetSession(_sessionManager, _authContext, Request);

            var generalCommand = new GeneralCommand
            {
                Name = command,
                ControllingUserId = currentSession.UserId
            };

            _sessionManager.SendGeneralCommand(currentSession.Id, sessionId, generalCommand, CancellationToken.None);

            return NoContent();
        }

        /// <summary>
        /// Issues a full general command to a client.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="command">The <see cref="GeneralCommand"/>.</param>
        /// <response code="204">Full general command sent to session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/Command")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult SendFullGeneralCommand(
            [FromRoute] string? sessionId,
            [FromBody, Required] GeneralCommand command)
        {
            var currentSession = RequestHelpers.GetSession(_sessionManager, _authContext, Request);

            if (command == null)
            {
                throw new ArgumentException("Request body may not be null");
            }

            command.ControllingUserId = currentSession.UserId;

            _sessionManager.SendGeneralCommand(
                currentSession.Id,
                sessionId,
                command,
                CancellationToken.None);

            return NoContent();
        }

        /// <summary>
        /// Issues a command to a client to display a message to the user.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="text">The message test.</param>
        /// <param name="header">The message header.</param>
        /// <param name="timeoutMs">The message timeout. If omitted the user will have to confirm viewing the message.</param>
        /// <response code="204">Message sent.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/Message")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult SendMessageCommand(
            [FromRoute] string? sessionId,
            [FromQuery] string? text,
            [FromQuery] string? header,
            [FromQuery] long? timeoutMs)
        {
            var command = new MessageCommand
            {
                Header = string.IsNullOrEmpty(header) ? "Message from Server" : header,
                TimeoutMs = timeoutMs,
                Text = text
            };

            _sessionManager.SendMessageCommand(RequestHelpers.GetSession(_sessionManager, _authContext, Request).Id, sessionId, command, CancellationToken.None);

            return NoContent();
        }

        /// <summary>
        /// Adds an additional user to a session.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="userId">The user id.</param>
        /// <response code="204">User added to session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/{sessionId}/User/{userId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult AddUserToSession(
            [FromRoute] string? sessionId,
            [FromRoute] Guid userId)
        {
            _sessionManager.AddAdditionalUser(sessionId, userId);
            return NoContent();
        }

        /// <summary>
        /// Removes an additional user from a session.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="userId">The user id.</param>
        /// <response code="204">User removed from session.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpDelete("Sessions/{sessionId}/User/{userId}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult RemoveUserFromSession(
            [FromRoute] string? sessionId,
            [FromRoute] Guid userId)
        {
            _sessionManager.RemoveAdditionalUser(sessionId, userId);
            return NoContent();
        }

        /// <summary>
        /// Updates capabilities for a device.
        /// </summary>
        /// <param name="id">The session id.</param>
        /// <param name="playableMediaTypes">A list of playable media types, comma delimited. Audio, Video, Book, Photo.</param>
        /// <param name="supportedCommands">A list of supported remote control commands, comma delimited.</param>
        /// <param name="supportsMediaControl">Determines whether media can be played remotely..</param>
        /// <param name="supportsSync">Determines whether sync is supported.</param>
        /// <param name="supportsPersistentIdentifier">Determines whether the device supports a unique identifier.</param>
        /// <response code="204">Capabilities posted.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/Capabilities")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult PostCapabilities(
            [FromQuery] string? id,
            [FromQuery] string? playableMediaTypes,
            [FromQuery] string? supportedCommands,
            [FromQuery] bool supportsMediaControl = false,
            [FromQuery] bool supportsSync = false,
            [FromQuery] bool supportsPersistentIdentifier = true)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                id = RequestHelpers.GetSession(_sessionManager, _authContext, Request).Id;
            }

            _sessionManager.ReportCapabilities(id, new ClientCapabilities
            {
                PlayableMediaTypes = RequestHelpers.Split(playableMediaTypes, ',', true),
                SupportedCommands = RequestHelpers.Split(supportedCommands, ',', true),
                SupportsMediaControl = supportsMediaControl,
                SupportsSync = supportsSync,
                SupportsPersistentIdentifier = supportsPersistentIdentifier
            });
            return NoContent();
        }

        /// <summary>
        /// Updates capabilities for a device.
        /// </summary>
        /// <param name="id">The session id.</param>
        /// <param name="capabilities">The <see cref="ClientCapabilities"/>.</param>
        /// <response code="204">Capabilities updated.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/Capabilities/Full")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult PostFullCapabilities(
            [FromQuery] string? id,
            [FromBody, Required] ClientCapabilities capabilities)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                id = RequestHelpers.GetSession(_sessionManager, _authContext, Request).Id;
            }

            _sessionManager.ReportCapabilities(id, capabilities);

            return NoContent();
        }

        /// <summary>
        /// Reports that a session is viewing an item.
        /// </summary>
        /// <param name="sessionId">The session id.</param>
        /// <param name="itemId">The item id.</param>
        /// <response code="204">Session reported to server.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/Viewing")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult ReportViewing(
            [FromQuery] string? sessionId,
            [FromQuery] string? itemId)
        {
            string session = RequestHelpers.GetSession(_sessionManager, _authContext, Request).Id;

            _sessionManager.ReportNowViewingItem(session, itemId);
            return NoContent();
        }

        /// <summary>
        /// Reports that a session has ended.
        /// </summary>
        /// <response code="204">Session end reported to server.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("Sessions/Logout")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult ReportSessionEnded()
        {
            AuthorizationInfo auth = _authContext.GetAuthorizationInfo(Request);

            _sessionManager.Logout(auth.Token);
            return NoContent();
        }

        /// <summary>
        /// Get all auth providers.
        /// </summary>
        /// <response code="200">Auth providers retrieved.</response>
        /// <returns>An <see cref="IEnumerable{NameIdPair}"/> with the auth providers.</returns>
        [HttpGet("Auth/Providers")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<NameIdPair>> GetAuthProviders()
        {
            return _userManager.GetAuthenticationProviders();
        }

        /// <summary>
        /// Get all password reset providers.
        /// </summary>
        /// <response code="200">Password reset providers retrieved.</response>
        /// <returns>An <see cref="IEnumerable{NameIdPair}"/> with the password reset providers.</returns>
        [HttpGet("Auto/PasswordResetProviders")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<NameIdPair>> GetPasswordResetProviders()
        {
            return _userManager.GetPasswordResetProviders();
        }
    }
}