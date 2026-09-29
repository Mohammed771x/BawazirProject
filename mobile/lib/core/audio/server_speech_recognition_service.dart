import 'dart:async';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:path_provider/path_provider.dart';
import 'package:record/record.dart';

import '../api/wordos_api.dart';
import 'speech_recognition_service.dart';

/// Speech recognition done by the server rather than the phone (ADR-107).
///
/// The phone's recogniser — Android's above all — misheard learners badly
/// enough that the transcript Speaking is judged on was often not what they
/// said. This records the turn instead and hands the audio to the backend,
/// which asks Gemini and falls back to Groq's Whisper.
///
/// It keeps the exact shape of [SpeechRecognitionService], so every screen
/// that already runs a push-to-talk turn — the Speaking conversation, the
/// spoken placement answers — works unchanged: press to start, press to
/// stop, read the draft, correct it, send it (ADR-028, ADR-069).
///
/// What it gives up is the words appearing while the learner talks: nothing
/// is known until the recording reaches the server. That is the trade the
/// product owner chose — a transcript that is right, a few seconds later,
/// over one that is live and wrong.
///
/// Nothing here judges anything, and nothing is kept: the recording is a
/// temporary file deleted the moment it has been read (rule R1, rule R4).
class ServerSpeechRecognitionService extends SpeechRecognitionService {
  ServerSpeechRecognitionService({
    required this.transcribe,
    AudioRecorder? recorder,
    Future<Directory> Function()? directory,
  }) : _recorder = recorder ?? AudioRecorder(),
       _directory = directory ?? getTemporaryDirectory;

  /// Sends the recording to the server — `WordOsApi.transcribeSpeech`.
  final Future<String> Function(Uint8List audio, String mimeType) transcribe;
  final AudioRecorder _recorder;
  final Future<Directory> Function() _directory;

  /// What the recorder writes, and what the server is told it is.
  static const mimeType = 'audio/mp4';

  /// 16 kHz mono is what both engines resample to anyway; recording more only
  /// makes the upload slower. 32 kbit/s AAC is about 4 KB a second — a minute
  /// of talking uploads in well under a second on any working connection.
  static const _config = RecordConfig(
    encoder: AudioEncoder.aacLc,
    sampleRate: 16000,
    numChannels: 1,
    bitRate: 32000,
  );

  /// Shorter than this is a tap, not a turn: the file holds little more than
  /// its own header, and sending it would spend a provider call on silence.
  static const minimumBytes = 2000;

  bool _initialised = false;
  bool _available = false;
  bool _recording = false;
  String? _path;
  String? _failure;

  @override
  bool get isAvailable => _available;

  @override
  bool get isListening => _recording;

  /// Always empty while recording: nothing is recognised until the turn ends.
  @override
  String get heard => '';

  @override
  String? get lastFailure => _failure;

  @override
  Future<bool> initialise() async {
    if (_initialised) return _available;
    _initialised = true;
    try {
      // Asks on first use, which is when the learner opens a Speaking turn
      // and can see why the microphone is wanted.
      _available = await _recorder.hasPermission();
    } catch (e) {
      debugPrint('WordOS recorder permission check failed: $e');
      _available = false;
    }
    return _available;
  }

  @override
  Future<bool> startListening({void Function(String heard)? onPartial}) async {
    if (!await initialise()) return false;
    if (_recording) return true;
    _failure = null;

    try {
      // The temporary directory is not guaranteed to exist, and the recorder
      // does not create it — a clip written into a missing folder is silently
      // nowhere. Found on the test bench before it could reach a learner.
      final folder = Directory('${(await _directory()).path}/speaking');
      await folder.create(recursive: true);
      final path =
          '${folder.path}/turn_${DateTime.now().microsecondsSinceEpoch}.m4a';

      await _recorder.start(_config, path: path);
      _path = path;
      _recording = true;
      return true;
    } catch (e) {
      debugPrint('WordOS recorder failed to start: $e');
      _recording = false;
      _path = null;
      return false;
    }
  }

  @override
  Future<String?> stopAndRead() async {
    if (!_recording) return null;
    _recording = false;

    File? file;
    try {
      final path = await _recorder.stop() ?? _path;
      _path = null;
      if (path == null) return null;

      file = File(path);
      if (!await file.exists()) return null;
      final audio = await file.readAsBytes();
      if (audio.length < minimumBytes) return null;

      final text = (await transcribe(audio, mimeType)).trim();
      return text.isEmpty ? null : text;
    } on ApiException catch (e) {
      debugPrint('WordOS transcription failed: ${e.code} ${e.message}');
      _failure = e.code;
      return null;
    } catch (e) {
      debugPrint('WordOS transcription failed: $e');
      _failure = 'UNEXPECTED';
      return null;
    } finally {
      await _delete(file);
    }
  }

  /// Not used by any screen — the learner-facing turns are push-to-talk — but
  /// kept working for the contract: record for [listenFor], then read.
  @override
  Future<String?> listenOnce({
    Duration pauseFor = const Duration(seconds: 3),
    Duration listenFor = const Duration(seconds: 45),
  }) async {
    if (!await startListening()) return null;
    await Future<void>.delayed(listenFor);
    return stopAndRead();
  }

  @override
  Future<void> stop() async {
    if (!_recording) return;
    await cancel();
  }

  /// Throws the recording away — the bin beside the microphone (ADR-059).
  @override
  Future<void> cancel() async {
    final wasRecording = _recording;
    _recording = false;
    final path = _path;
    _path = null;
    if (!wasRecording) return;
    try {
      // `cancel` stops and deletes the file itself.
      await _recorder.cancel();
    } catch (e) {
      debugPrint('WordOS recorder cancel failed: $e');
    }
    if (path != null) await _delete(File(path));
  }

  /// Releases the native recorder. The provider calls this on disposal.
  Future<void> dispose() async {
    try {
      await _recorder.dispose();
    } catch (_) {}
  }

  Future<void> _delete(File? file) async {
    if (file == null) return;
    try {
      if (await file.exists()) await file.delete();
    } catch (_) {
      // A temporary file the OS will clear anyway; not worth failing a turn.
    }
  }
}
