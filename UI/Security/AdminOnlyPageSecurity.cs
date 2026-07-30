// ManageComingSoon - Admin-only page security helper
//
// Confirmed via ILSpy (Emby.Web.GenericUI.dll):
//   PluginPageControllerHost.CheckUserAuthorization only calls
//   IPluginPageSecurity.CheckIsUserAuthorised when the page controller
//   implements that interface. Without it, EnableInUserMenu/EnableInMainMenu
//   are menu-visibility hints only — there is NO server-side access check.
//   Tab controllers are registered independently of their parent (own
//   pageId), so each one needing admin-only enforcement must implement
//   this itself, not just the top-level controller.
//
// NOT confirmed via ILSpy: GenericUIApiService.Get/RunCommand have no
// dedicated try/catch around the pages-manager call - any exception thrown
// here propagates unhandled to ServiceStack's default exception pipeline.
// UnauthorizedAccessException is used below as the conventional .NET/
// ServiceStack type for this (normally mapped to 401), but that mapping
// itself was not traced. If this surfaces as a raw 500 instead of a clean
// "not authorised" response after deploying, this is the first place to
// revisit.

namespace ManageComingSoon.UI.Security
{
    using System;
    using System.Threading.Tasks;
    using MediaBrowser.Model.Dto;
    using MediaBrowser.Model.Plugins.UI.Views;

    internal static class AdminOnlyPageSecurity
    {
        public static Task CheckIsAdmin(UserDto user, IPluginUIView requestedView)
        {
            if (user == null || user.Policy == null || !user.Policy.IsAdministrator)
            {
                throw new UnauthorizedAccessException(
                    "This page is available to administrators only.");
            }

            return Task.CompletedTask;
        }
    }
}