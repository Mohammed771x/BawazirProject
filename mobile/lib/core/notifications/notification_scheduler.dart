import 'package:flutter/foundation.dart';
import 'package:flutter_local_notifications/flutter_local_notifications.dart';
import 'package:flutter_timezone/flutter_timezone.dart';
import 'package:timezone/data/latest_all.dart' as tz_data;
import 'package:timezone/timezone.dart' as tz;

/// One notification, already written, waiting for a time.
///
/// [title] and [body] are finished sentences in the learner's language: by the
/// time one of these exists the decision of *what to say* has been made (the
/// server) and the decision of *how to say it* has been made (AppStrings). This
/// layer only knows when.
@immutable
class ScheduledNotification {
  const ScheduledNotification({
    required this.id,
    required this.title,
    required this.body,
    required this.at,
  });

  /// Stable within one scheduling pass. Every pass cancels what it replaces, so
  /// these never have to be unique across time — only within a list.
  final int id;
  final String title;
  final String body;

  /// Local wall-clock time. The device's own timezone, because that is what a
  /// learner means by "eight in the morning".
  final DateTime at;
}

/// What the app can ask the phone to do about notifications.
///
/// An interface with two implementations for the same reason
/// [WordOsApi] has: the real one needs a platform channel that does not exist
/// in a test binary, and every rule worth testing — which reminders are kept,
/// what they say, what happens when permission is refused — lives above it.
abstract class NotificationScheduler {
  /// Prepares the platform and asks for permission if it has not been asked.
  ///
  /// Returns whether notifications may actually be shown. False is an ordinary
  /// answer, not a failure: a learner is allowed to say no, and on iOS and on
  /// Android 13 and up they are asked.
  Future<bool> initialize();

  /// Replaces every reminder this app has pending with [notifications].
  ///
  /// Replaces rather than adds, always. These are refreshed each time the app
  /// opens and each one names a specific day, so anything already pending is
  /// either about to be rewritten with a newer count or is about a day that has
  /// since been answered — and leaving it in place would have the phone say
  /// something the server no longer believes.
  Future<void> schedule(List<ScheduledNotification> notifications);

  /// Removes every pending reminder. What "turn reminders off" does.
  Future<void> cancelAll();
}

/// The real one, over `flutter_local_notifications`.
///
/// No Firebase and no server push (ADR-076): every notification here is an
/// alarm the phone sets for itself, which is why it works offline and why it
/// needs the whole message decided in advance.
class DeviceNotificationScheduler implements NotificationScheduler {
  DeviceNotificationScheduler([FlutterLocalNotificationsPlugin? plugin])
      : _plugin = plugin ?? FlutterLocalNotificationsPlugin();

  final FlutterLocalNotificationsPlugin _plugin;

  bool _ready = false;

  /// Android needs a channel before anything can be posted to it. The name and
  /// description are what the learner sees in the system settings for this app,
  /// so they are said in a way that makes sense out of context.
  static const _channel = AndroidNotificationChannel(
    'wordos.reminders',
    'Daily reminders',
    description: 'Your morning and evening practice reminders.',
    importance: Importance.defaultImportance,
  );

  @override
  Future<bool> initialize() async {
    if (!_ready) {
      // The timezone database, then *this* phone's zone. Scheduling needs both:
      // "8am tomorrow" is a wall-clock time, and turning it into an instant is
      // exactly what a timezone is for. Without this every reminder would be
      // scheduled in UTC and arrive at the wrong hour everywhere but London.
      tz_data.initializeTimeZones();
      try {
        final zone = await FlutterTimezone.getLocalTimezone();
        tz.setLocalLocation(tz.getLocation(zone.identifier));
      } catch (_) {
        // A zone name the database does not have. UTC is wrong but working;
        // refusing to schedule anything would be worse.
      }

      await _plugin.initialize(
        settings: const InitializationSettings(
          android: AndroidInitializationSettings('@mipmap/ic_launcher'),
          // All three false: permission is asked for below, after the learner
          // has seen what the app is. Asking during initialize means asking on
          // first launch, before the tour — the surest way to be told no.
          iOS: DarwinInitializationSettings(
            requestAlertPermission: false,
            requestBadgePermission: false,
            requestSoundPermission: false,
          ),
        ),
      );

      await _plugin
          .resolvePlatformSpecificImplementation<
              AndroidFlutterLocalNotificationsPlugin>()
          ?.createNotificationChannel(_channel);

      _ready = true;
    }

    final android = _plugin.resolvePlatformSpecificImplementation<
        AndroidFlutterLocalNotificationsPlugin>();
    if (android != null) {
      // Android 13+. Older versions have no such permission and answer null,
      // which is a yes.
      return await android.requestNotificationsPermission() ?? true;
    }

    final ios = _plugin.resolvePlatformSpecificImplementation<
        IOSFlutterLocalNotificationsPlugin>();
    if (ios != null) {
      return await ios.requestPermissions(alert: true, badge: true, sound: true) ??
          false;
    }

    // A platform with no notifications at all — the test binary, or desktop.
    return false;
  }

  @override
  Future<void> schedule(List<ScheduledNotification> notifications) async {
    await cancelAll();

    for (final notification in notifications) {
      final at = tz.TZDateTime.from(notification.at, tz.local);
      if (!at.isAfter(tz.TZDateTime.now(tz.local))) continue;

      await _plugin.zonedSchedule(
        id: notification.id,
        title: notification.title,
        body: notification.body,
        scheduledDate: at,
        notificationDetails: NotificationDetails(
          android: AndroidNotificationDetails(
            _channel.id,
            _channel.name,
            channelDescription: _channel.description,
            importance: Importance.defaultImportance,
            priority: Priority.defaultPriority,
          ),
          iOS: const DarwinNotificationDetails(),
        ),
        // Inexact on purpose. An exact alarm needs SCHEDULE_EXACT_ALARM, which
        // Android treats as a special permission the learner must grant in
        // system settings — a large price for a reminder that does not care
        // whether it arrives at 08:00 or 08:09.
        androidScheduleMode: AndroidScheduleMode.inexactAllowWhileIdle,
      );
    }
  }

  @override
  Future<void> cancelAll() => _plugin.cancelAll();
}
