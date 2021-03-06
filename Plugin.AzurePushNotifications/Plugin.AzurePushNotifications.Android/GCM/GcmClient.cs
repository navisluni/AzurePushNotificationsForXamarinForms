using System;
using System.Collections.Generic;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;

namespace Gcm.Client
{
    public class GcmClient
    {
        private const string BackoffMs = "backoff_ms";
        private const string GsfPackage = "com.google.android.gsf";
        private const string Preferences = "com.google.android.gcm";
        private const int DefaultBackoffMs = 3000;
        private const string PropertyRegId = "regId";
        private const string PropertyAppVersion = "appVersion";
        private const string PropertyOnServer = "onServer";
        public static Activity MainActivity;

        //static GCMBroadcastReceiver sRetryReceiver;

        public static void CheckDevice(Context context)
        {
            var version = (int)Build.VERSION.SdkInt;
            if(version < 8)
            {
                throw new InvalidOperationException("Device must be at least API Level 8 (instead of " + version + ")");
            }

            var packageManager = context.PackageManager;

            try
            {
                packageManager.GetPackageInfo(GsfPackage, 0);
            }
            catch
            {
                throw new InvalidOperationException("Device does not have package " + GsfPackage);
            }
        }

        public static void CheckManifest(Context context)
        {
            var packageManager = context.PackageManager;
            var packageName = context.PackageName;
            var permissionName = packageName + ".permission.C2D_MESSAGE";

            if(string.IsNullOrEmpty(packageName))
            {
                throw new NotSupportedException("Your Android app must have a package name!");
            }

            if(char.IsUpper(packageName[0]))
            {
                throw new NotSupportedException("Your Android app package name MUST start with a lowercase character.  Current Package Name: " + packageName);
            }

            try
            {
                packageManager.GetPermissionInfo(permissionName, PackageInfoFlags.Permissions);
            }
            catch
            {
                throw new AccessViolationException("Application does not define permission: " + permissionName);
            }

            PackageInfo receiversInfo;

            try
            {
                receiversInfo = packageManager.GetPackageInfo(packageName, PackageInfoFlags.Receivers);
            }
            catch
            {
                throw new InvalidOperationException("Could not get receivers for package " + packageName);
            }

            var receivers = receiversInfo.Receivers;

            if(receivers == null || receivers.Count <= 0)
            {
                throw new InvalidOperationException("No Receiver for package " + packageName);
            }

            //Logger.Debug("number of receivers for " + packageName + ": " + receivers.Count);

            var allowedReceivers = new HashSet<string>();

            foreach(var receiver in receivers)
            {
                if(Constants.PermissionGcmIntents.Equals(receiver.Permission))
                {
                    allowedReceivers.Add(receiver.Name);
                }
            }

            if(allowedReceivers.Count <= 0)
            {
                throw new InvalidOperationException("No receiver allowed to receive " + Constants.PermissionGcmIntents);
            }

            CheckReceiver(context, allowedReceivers, Constants.IntentFromGcmRegistrationCallback);
            CheckReceiver(context, allowedReceivers, Constants.IntentFromGcmMessage);
        }

        private static void CheckReceiver(Context context, HashSet<string> allowedReceivers, string action)
        {
            var pm = context.PackageManager;
            var packageName = context.PackageName;

            var intent = new Intent(action);
            intent.SetPackage(packageName);

            var receivers = pm.QueryBroadcastReceivers(intent, PackageInfoFlags.IntentFilters);

            if(receivers == null || receivers.Count <= 0)
            {
                throw new InvalidOperationException("No receivers for action " + action);
            }

            //Logger.Debug("Found " + receivers.Count + " receivers for action " + action);

            foreach(var receiver in receivers)
            {
                var name = receiver.ActivityInfo.Name;
                if(!allowedReceivers.Contains(name))
                {
                    throw new InvalidOperationException("Receiver " + name + " is not set with permission " + Constants.PermissionGcmIntents);
                }
            }
        }

        public static void Register(Context context, params string[] senderIds)
        {
            SetRetryBroadcastReceiver(context);
            ResetBackoff(context);

            InternalRegister(context, senderIds);
        }

        internal static void InternalRegister(Context context, params string[] senderIds)
        {
            if(senderIds == null || senderIds.Length <= 0)
            {
                throw new ArgumentException("No senderIds");
            }

            var senders = string.Join(",", senderIds);

            //Logger.Debug("Registering app " + context.PackageName + " of senders " + senders);

            var intent = new Intent(Constants.IntentToGcmRegistration);
            intent.SetPackage(GsfPackage);
            intent.PutExtra(Constants.ExtraApplicationPendingIntent,
                PendingIntent.GetBroadcast(context, 0, new Intent(), 0));
            intent.PutExtra(Constants.ExtraSender, senders);

            context.StartService(intent);
        }

        public static void UnRegister(Context context)
        {
            SetRetryBroadcastReceiver(context);
            ResetBackoff(context);
            InternalUnRegister(context);
        }

        internal static void InternalUnRegister(Context context)
        {
            //Logger.Debug("Unregistering app " + context.PackageName);

            var intent = new Intent(Constants.IntentToGcmUnregistration);
            intent.SetPackage(GsfPackage);
            intent.PutExtra(Constants.ExtraApplicationPendingIntent,
                PendingIntent.GetBroadcast(context, 0, new Intent(), 0));

            context.StartService(intent);
        }

        private static void SetRetryBroadcastReceiver(Context context)
        {
            //if (sRetryReceiver == null)
            //{
            //    sRetryReceiver = new GCMBroadcastReceiver();
            //    var category = context.PackageName;

            //    var filter = new IntentFilter(GCMConstants.INTENT_FROM_GCM_LIBRARY_RETRY);
            //    filter.AddCategory(category);

            //    var permission = category + ".permission.C2D_MESSAGE";

            //    Log.Verbose(TAG, "Registering receiver");

            //    context.RegisterReceiver(sRetryReceiver, filter, permission, null);
            //}
        }

        public static string GetRegistrationId(Context context)
        {
            var prefs = GetGcmPreferences(context);

            var registrationId = prefs.GetString(PropertyRegId, "");

            var oldVersion = prefs.GetInt(PropertyAppVersion, int.MinValue);
            var newVersion = GetAppVersion(context);

            if(oldVersion != int.MinValue && oldVersion != newVersion)
            {
                //Logger.Debug("App version changed from " + oldVersion + " to " + newVersion + "; resetting registration id");

                ClearRegistrationId(context);
                registrationId = string.Empty;
            }

            return registrationId;
        }

        public static bool IsRegistered(Context context)
        {
            var registrationId = GetRegistrationId(context);

            return !string.IsNullOrEmpty(registrationId);
        }

        internal static string ClearRegistrationId(Context context)
        {
            return SetRegistrationId(context, "");
        }

        internal static string SetRegistrationId(Context context, string registrationId)
        {
            var prefs = GetGcmPreferences(context);

            var oldRegistrationId = prefs.GetString(PropertyRegId, "");
            var appVersion = GetAppVersion(context);
            //Logger.Debug("Saving registrationId on app version " + appVersion);
            var editor = prefs.Edit();
            editor.PutString(PropertyRegId, registrationId);
            editor.PutInt(PropertyAppVersion, appVersion);
            editor.Commit();
            return oldRegistrationId;
        }

        public static void SetRegisteredOnServer(Context context, bool flag)
        {
            var prefs = GetGcmPreferences(context);
            // Logger.Debug("Setting registered on server status as: " + flag);
            var editor = prefs.Edit();
            editor.PutBoolean(PropertyOnServer, flag);
            editor.Commit();
        }

        public static bool IsRegisteredOnServer(Context context)
        {
            var prefs = GetGcmPreferences(context);
            var isRegistered = prefs.GetBoolean(PropertyOnServer, false);
            // Logger.Debug("Is registered on server: " + isRegistered);
            return isRegistered;
        }

        private static int GetAppVersion(Context context)
        {
            try
            {
                var packageInfo = context.PackageManager.GetPackageInfo(context.PackageName, 0);
                return packageInfo.VersionCode;
            }
            catch
            {
                throw new InvalidOperationException("Could not get package name");
            }
        }

        internal static void ResetBackoff(Context context)
        {
            // Logger.Debug("resetting backoff for " + context.PackageName);
            SetBackoff(context, DefaultBackoffMs);
        }

        internal static int GetBackoff(Context context)
        {
            var prefs = GetGcmPreferences(context);
            return prefs.GetInt(BackoffMs, DefaultBackoffMs);
        }

        internal static void SetBackoff(Context context, int backoff)
        {
            var prefs = GetGcmPreferences(context);
            var editor = prefs.Edit();
            editor.PutInt(BackoffMs, backoff);
            editor.Commit();
        }

        private static ISharedPreferences GetGcmPreferences(Context context)
        {
            return context.GetSharedPreferences(Preferences, FileCreationMode.Private);
        }
    }
}