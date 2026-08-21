import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/support/support_contact.dart';

/// The link itself (ADR-055).
///
/// Worth a test because every failure mode here is silent: a stray plus, a
/// space, or the wrong scheme produces a page that says the number is invalid,
/// and the learner is left believing nobody wants to hear from them.
void main() {
  test('the WhatsApp link is the exact form wa.me accepts', () {
    final uri = SupportContact.whatsAppUri;

    expect(uri.scheme, 'https');
    expect(uri.host, 'wa.me');
    expect(uri.path, '/917558973719');

    // The whole link, spelled out — this is the string the learner's phone
    // actually receives.
    expect(uri.toString(), 'https://wa.me/917558973719');
  });

  test('the number carries no punctuation the link cannot take', () {
    expect(SupportContact.whatsAppNumber,
        matches(RegExp(r'^[0-9]+$')),
        reason: 'wa.me takes digits only — no plus, no spaces, no dashes');

    // Country code and subscriber number, kept together as one string, which
    // is what wa.me wants.
    expect(SupportContact.whatsAppNumber, startsWith('91'));
  });

  test('what a person reads matches what the link dials', () {
    final readable = SupportContact.displayNumber
        .replaceAll(RegExp(r'[^0-9]'), '');

    // The two are written separately — one for eyes, one for the URL — so they
    // can drift apart. They must not.
    expect(readable, SupportContact.whatsAppNumber);
  });
}
