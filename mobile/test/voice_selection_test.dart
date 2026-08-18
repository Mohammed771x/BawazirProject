import 'package:flutter_test/flutter_test.dart';
import 'package:wordos/core/audio/speech_provider.dart';

/// Which voice the tutor speaks with.
///
/// Reported from the device: the voice used to be the ordinary system one and
/// had become unpleasant. The cause was the ranking — it treated *any*
/// unrecognised voice as better than the built-in `compact` one, and on iOS the
/// unrecognised list includes Zarvox, Trinoids and Bad News.
///
/// The rule this pins is one-directional: upgrade the voice when the device
/// genuinely has a better one, and otherwise leave the platform's own choice
/// completely alone.
void main() {
  Map<String, String> voice(String name, {String locale = 'en-US', String? id}) =>
      {'name': name, 'locale': locale, 'identifier': ?id};

  test('a novelty voice never becomes the tutor', () {
    final chosen = bestEnglishVoice([
      voice('Zarvox'),
      voice('Trinoids'),
      voice('Bad News'),
      voice('Fred'),
      voice('Whisper'),
    ]);

    expect(chosen, isNull,
        reason: 'none of these is a usable voice — keep the system default');
  });

  test('an unremarkable voice does not displace the system default', () {
    // "Samantha (compact)" is what iOS would have chosen anyway. Replacing it
    // with an equally unmarked voice is churn at best and a regression at
    // worst — so nothing is chosen.
    final chosen = bestEnglishVoice([
      voice('Samantha', id: 'com.apple.voice.compact.en-US.Samantha'),
      voice('Daniel', locale: 'en-GB'),
    ]);

    expect(chosen, isNull);
  });

  test('a genuinely better voice is taken', () {
    final chosen = bestEnglishVoice([
      voice('Samantha', id: 'com.apple.voice.compact.en-US.Samantha'),
      voice('Zarvox'),
      voice('Ava', id: 'com.apple.voice.premium.en-US.Ava'),
    ]);

    expect(chosen?['name'], 'Ava');
  });

  test('premium beats enhanced, and en-US breaks the tie', () {
    final chosen = bestEnglishVoice([
      voice('Serena', locale: 'en-GB', id: 'com.apple.voice.enhanced.en-GB.Serena'),
      voice('Evan', id: 'com.apple.voice.enhanced.en-US.Evan'),
      voice('Ava', id: 'com.apple.voice.premium.en-US.Ava'),
    ]);

    expect(chosen?['name'], 'Ava');

    final noPremium = bestEnglishVoice([
      voice('Serena', locale: 'en-GB', id: 'com.apple.voice.enhanced.en-GB.Serena'),
      voice('Evan', id: 'com.apple.voice.enhanced.en-US.Evan'),
    ]);

    expect(noPremium?['name'], 'Evan', reason: 'en-US matches the content');
  });

  test('a non-English voice is never chosen, however good it claims to be', () {
    final chosen = bestEnglishVoice([
      voice('Maged', locale: 'ar-SA', id: 'com.apple.voice.premium.ar-SA.Maged'),
      voice('Samantha', id: 'com.apple.voice.compact.en-US.Samantha'),
    ]);

    // The learning content is English; an Arabic voice reading it is worse
    // than any English one.
    expect(chosen, isNull);
  });

  test('an empty or unusable voice list changes nothing', () {
    expect(bestEnglishVoice([]), isNull);
    expect(bestEnglishVoice([{}]), isNull);
  });
}
