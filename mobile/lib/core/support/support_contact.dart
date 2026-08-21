import 'package:flutter/foundation.dart';
import 'package:url_launcher/url_launcher.dart';

/// Where "contact support" goes (ADR-055).
///
/// One number, written down once. It is not a secret — every learner who taps
/// the button sees it — so it lives in the client rather than behind an
/// endpoint: a chat link needs no server, no token and no round trip, and
/// putting one in the way would only mean the button fails when the network
/// does.
///
/// The trade is that changing it needs a new build. That is the right way round
/// for a number that belongs to one person and changes about never.
class SupportContact {
  const SupportContact._();

  /// International form, digits only — no plus, no spaces. `wa.me` wants it
  /// exactly like this, and a stray character is the difference between the
  /// chat opening and an "invalid phone number" page.
  static const String whatsAppNumber = '917558973719';

  /// What a person reads, as opposed to what the link uses.
  static const String displayNumber = '+91 7558973719';

  /// The universal link. `wa.me` opens the app directly when it is installed
  /// and falls back to the browser when it is not, which is why it is used
  /// instead of the `whatsapp://` scheme — that one needs an iOS entitlement
  /// and shows the learner nothing at all when WhatsApp is missing.
  static Uri get whatsAppUri => Uri.parse('https://wa.me/$whatsAppNumber');

  /// Opens the chat. Returns false when nothing could handle it, so the caller
  /// can show the number instead of leaving the learner with a dead button.
  static Future<bool> openWhatsApp() async {
    try {
      return await launchUrl(
        whatsAppUri,
        // Handed to WhatsApp — or to the browser, which redirects there.
        // Staying in-app would put a chat inside a webview nobody can log into.
        mode: LaunchMode.externalApplication,
      );
    } catch (error) {
      debugPrint('support: could not open WhatsApp: $error');
      return false;
    }
  }
}
