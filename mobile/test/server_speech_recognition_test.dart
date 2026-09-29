import 'dart:io';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:record/record.dart';
import 'package:wordos/core/api/wordos_api.dart';
import 'package:wordos/core/audio/server_speech_recognition_service.dart';

/// The server recogniser (ADR-107): the phone records, the backend writes it
/// down. The platform recorder is faked — a test binary has no microphone —
/// and so is the API, so what is under test is the turn itself: what is sent,
/// what comes back, and that the recording never outlives it.
void main() {
  late Directory temp;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('wordos_stt_test');
  });

  tearDown(() async {
    if (await temp.exists()) await temp.delete(recursive: true);
  });

  ServerSpeechRecognitionService build(
    _FakeRecorder recorder,
    Future<String> Function(Uint8List audio, String mimeType) transcribe,
  ) =>
      ServerSpeechRecognitionService(
        transcribe: transcribe,
        recorder: recorder,
        directory: () async => temp,
      );

  test('records, sends the audio, and returns what the server heard',
      () async {
    final recorder = _FakeRecorder(bytes: 8000);
    Uint8List? sent;
    String? sentType;
    final service = build(recorder, (audio, type) async {
      sent = audio;
      sentType = type;
      return '  He don\'t like coffee.  ';
    });

    expect(await service.startListening(), isTrue);
    expect(service.isListening, isTrue);
    final heard = await service.stopAndRead();

    // Verbatim — the learner's grammar is theirs to fix, not the recogniser's.
    expect(heard, "He don't like coffee.");
    expect(sent!.length, 8000);
    expect(sentType, 'audio/mp4');
    expect(service.isListening, isFalse);
    expect(service.lastFailure, isNull);
    // Nothing is kept on the phone once the turn is written down.
    expect(File(recorder.path!).existsSync(), isFalse);
  });

  test('records 16 kHz mono AAC, which both engines accept', () async {
    final recorder = _FakeRecorder(bytes: 8000);
    final service = build(recorder, (_, _) async => 'ok');

    await service.startListening();

    expect(recorder.config!.encoder, AudioEncoder.aacLc);
    expect(recorder.config!.sampleRate, 16000);
    expect(recorder.config!.numChannels, 1);
    expect(recorder.path, endsWith('.m4a'));
  });

  test('creates the folder it records into', () async {
    // The macOS sandbox handed back a temporary directory that did not exist,
    // and the recorder silently wrote nowhere — found on the test bench.
    await temp.delete(recursive: true);
    final recorder = _FakeRecorder(bytes: 8000);
    final service = build(recorder, (_, _) async => 'hello');

    expect(await service.startListening(), isTrue);
    expect(await service.stopAndRead(), 'hello');
  });

  test('a tap too short to hold speech is not sent', () async {
    final recorder = _FakeRecorder(bytes: 300);
    var calls = 0;
    final service = build(recorder, (_, _) async {
      calls++;
      return 'should not be asked';
    });

    await service.startListening();
    expect(await service.stopAndRead(), isNull);
    expect(calls, 0, reason: 'a provider call spent on a header is waste');
    expect(service.lastFailure, isNull,
        reason: 'nothing failed — there was nothing to hear');
  });

  test('silence comes back as nothing, not as a failure', () async {
    final service = build(_FakeRecorder(bytes: 8000), (_, _) async => '   ');

    await service.startListening();
    expect(await service.stopAndRead(), isNull);
    expect(service.lastFailure, isNull);
  });

  test('a server that could not listen is a failure the screen can name',
      () async {
    final recorder = _FakeRecorder(bytes: 8000);
    final service = build(recorder, (_, _) async {
      throw const ApiException('SPEECH_UNAVAILABLE', 'down', statusCode: 503);
    });

    await service.startListening();
    expect(await service.stopAndRead(), isNull);
    expect(service.lastFailure, 'SPEECH_UNAVAILABLE');
    expect(File(recorder.path!).existsSync(), isFalse,
        reason: 'a failed turn still leaves nothing behind');
  });

  test('an unexpected error is a failure too, never a crash', () async {
    final service = build(_FakeRecorder(bytes: 8000), (_, _) async {
      throw StateError('socket closed');
    });

    await service.startListening();
    expect(await service.stopAndRead(), isNull);
    expect(service.lastFailure, 'UNEXPECTED');
  });

  test('the next turn starts with the last failure forgotten', () async {
    var fail = true;
    final service = build(_FakeRecorder(bytes: 8000), (_, _) async {
      if (fail) {
        throw const ApiException('SPEECH_UNAVAILABLE', 'down');
      }
      return 'fine now';
    });

    await service.startListening();
    await service.stopAndRead();
    expect(service.lastFailure, isNotNull);

    fail = false;
    await service.startListening();
    expect(service.lastFailure, isNull);
    expect(await service.stopAndRead(), 'fine now');
  });

  test('the bin throws the recording away and sends nothing', () async {
    final recorder = _FakeRecorder(bytes: 8000);
    var calls = 0;
    final service = build(recorder, (_, _) async {
      calls++;
      return 'x';
    });

    await service.startListening();
    await service.cancel();

    expect(recorder.cancelled, isTrue);
    expect(service.isListening, isFalse);
    expect(await service.stopAndRead(), isNull);
    expect(calls, 0);
  });

  test('without microphone permission it cannot listen, so typing is offered',
      () async {
    final service =
        build(_FakeRecorder(bytes: 8000, permitted: false), (_, _) async => 'x');

    expect(await service.initialise(), isFalse);
    expect(service.isAvailable, isFalse);
    expect(await service.startListening(), isFalse);
  });

  test('a recorder that fails to start is reported, not left half-open',
      () async {
    final service = build(
        _FakeRecorder(bytes: 8000, failStart: true), (_, _) async => 'x');

    expect(await service.startListening(), isFalse);
    expect(service.isListening, isFalse);
  });

  test('stopping when nothing is recording reads nothing', () async {
    final service = build(_FakeRecorder(bytes: 8000), (_, _) async => 'x');
    expect(await service.stopAndRead(), isNull);
  });
}

/// Writes a file of [bytes] where the real recorder would, on `stop`.
class _FakeRecorder implements AudioRecorder {
  _FakeRecorder({
    required this.bytes,
    this.permitted = true,
    this.failStart = false,
  });

  final int bytes;
  final bool permitted;
  final bool failStart;

  RecordConfig? config;
  String? path;
  bool cancelled = false;

  @override
  Future<bool> hasPermission({bool request = true}) async => permitted;

  @override
  Future<void> start(RecordConfig config, {required String path}) async {
    if (failStart) throw Exception('recorder busy');
    this.config = config;
    this.path = path;
  }

  @override
  Future<String?> stop() async {
    await File(path!).writeAsBytes(Uint8List(bytes));
    return path;
  }

  @override
  Future<void> cancel() async {
    cancelled = true;
  }

  @override
  Future<void> dispose() async {}

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
