import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/baseline_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/usecases/baseline_usecases.dart';

void main() {
  test('BaselineItemState matches the C# declaration order', () {
    // It crosses the wire as an ordinal, so the positions are the contract — see
    // .claude/rules/hand-mirrored-dtos.md. A member inserted on either side re-maps every value.
    expect(baselineItemStateFromJson(0), BaselineItemState.compliant);
    expect(baselineItemStateFromJson(1), BaselineItemState.drifted);
    expect(baselineItemStateFromJson(2), BaselineItemState.notApplicable);
    expect(baselineItemStateFromJson(3), BaselineItemState.error);
    expect(baselineItemStateFromJson('NotApplicable'), BaselineItemState.notApplicable);
    expect(baselineItemStateFromJson(null), isNull);
  });

  test('a host that has not reported an item reads as no state, not as compliant', () {
    final detail = baselineHostDetailFromJson({
      'hostId': 'h',
      'hostname': 'pc-1',
      'signedVersion': 2,
      'items': [
        {
          'item': {'key': 'copilot.off', 'kind': 'setting', 'scope': 'machine', 'hive': 'HKLM', 'path': r'SOFTWARE\P', 'valueName': 'V'},
          'state': null,
        },
      ],
      'facts': [],
    });

    expect(detail.items.single.state, isNull);
    expect(detail.items.single.item.location, r'HKLM\SOFTWARE\P\V');
  });

  test('the per-item counts are read from the detail', () {
    final detail = baselineDetailFromJson({
      'id': 'b',
      'name': 'Windows workstation',
      'assignedToAllWindowsHosts': true,
      'assignedHostNames': ['pc-1'],
      'versions': [
        {'id': 'v', 'number': 1, 'document': '{}', 'documentSha256': 'x', 'signed': true, 'createdUtc': '2026-11-12T00:00:00Z'},
      ],
      'items': [
        {
          'item': {'key': 'a', 'kind': 'setting', 'scope': 'machine', 'hive': 'HKLM', 'path': 'P', 'valueName': 'V'},
          'counts': {'compliant': 3, 'drifted': 1, 'notApplicable': 0, 'error': 0, 'notReported': 2},
        },
      ],
      'hosts': [],
    });

    expect(detail.signedVersion?.number, 1);
    expect(detail.items.single.counts.drifted, 1);
    expect(detail.items.single.counts.notReported, 2);
  });

  test('the sign hash is lowercase hex SHA-256 over UTF-8, as ScriptContentHash.Of is', () {
    // Known vectors: a change of encoding or case on either side fails here rather than as a 409.
    expect(
      SignBaselineVersion.documentSha256(''),
      'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    );
    expect(
      SignBaselineVersion.documentSha256('abc'),
      'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
    );
  });
}
