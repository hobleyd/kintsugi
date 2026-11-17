import 'dart:convert';

import 'package:crypto/crypto.dart';

import '../entities/baseline.dart';
import '../repositories/repositories.dart';

/// Every baseline with its compliance totals.
class GetBaselines {
  const GetBaselines(this._repository);

  final BaselineRepository _repository;

  Future<List<BaselineSummary>> call() => _repository.list();
}

/// One baseline: versions, per-item and per-host compliance.
class GetBaseline {
  const GetBaseline(this._repository);

  final BaselineRepository _repository;

  Future<BaselineDetail> call(String baselineId) => _repository.get(baselineId);
}

/// One host's item states and facts against a baseline.
class GetBaselineHost {
  const GetBaselineHost(this._repository);

  final BaselineRepository _repository;

  Future<BaselineHostDetail> call(String baselineId, String hostId) => _repository.host(baselineId, hostId);
}

/// Creates a baseline, or adds a version to [baselineId] when one is given, from pasted JSON.
class ImportBaseline {
  const ImportBaseline(this._repository);

  final BaselineRepository _repository;

  /// Throws [FormatException] for text that is not a JSON object, before anything is sent.
  Future<void> call(String json, {String? baselineId}) async {
    final decoded = jsonDecode(json);
    if (decoded is! Map<String, dynamic>) {
      throw const FormatException('A baseline is a JSON object with "name" and "items".');
    }
    await (baselineId == null ? _repository.create(decoded) : _repository.addVersion(baselineId, decoded));
  }
}

/// Signs a version over the bytes the reviewer was shown.
class SignBaselineVersion {
  const SignBaselineVersion(this._repository);

  final BaselineRepository _repository;

  /// The SHA-256 the server compares with `ScriptContentHash.Of` over the stored document: lowercase hex
  /// over UTF-8, the same rule as `InstructionsPanelBloc.scriptSha256`. Hashed here from the text this
  /// client received and displayed — never echoed from the server's own `documentSha256`, which would
  /// prove nothing about what was reviewed.
  static String documentSha256(String document) => sha256.convert(utf8.encode(document)).toString();

  Future<void> call(BaselineVersion version) => _repository.sign(version.id, documentSha256(version.document));
}

class AssignBaseline {
  const AssignBaseline(this._repository);

  final BaselineRepository _repository;

  Future<void> call(String baselineId, {required bool allWindowsHosts, required List<String> hostNames}) =>
      _repository.assign(baselineId, allWindowsHosts: allWindowsHosts, hostNames: hostNames);
}
