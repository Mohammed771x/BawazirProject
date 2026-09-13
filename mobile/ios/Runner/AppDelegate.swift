import Flutter
import UIKit
import UserNotifications

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    // Daily reminders (ADR-076). Without this delegate a notification that
    // arrives while the app is open is swallowed silently — which is exactly
    // the case a developer testing the feature hits first, and it looks like
    // nothing was ever scheduled.
    if #available(iOS 10.0, *) {
      UNUserNotificationCenter.current().delegate =
        self as? UNUserNotificationCenterDelegate
    }

    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)
  }
}
