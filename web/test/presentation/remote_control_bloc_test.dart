import 'dart:ui' as ui;

import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/remote_control_session.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/remote_control_usecases.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_control_bloc.dart';

/// Nothing here is called: these tests feed decoded tiles straight into the bloc, which is the
/// point where the picture is assembled and where the bug they pin lived.
class UnusedRemoteControlRepository implements RemoteControlRepository {
  @override
  Future<RemoteControlSession> request(String hostId) => throw UnimplementedError();

  @override
  Future<RemoteControlSession?> session(String id) => throw UnimplementedError();

  @override
  Future<void> end(String id) => throw UnimplementedError();

  @override
  RemoteControlStream openStream(String sessionId) => throw UnimplementedError();
}

const geometry = RemoteDisplayGeometry(
  pointWidth: 1920,
  pointHeight: 1080,
  imageWidth: 1600,
  imageHeight: 900,
);

Future<ui.Image> blankImage(int width, int height) {
  final recorder = ui.PictureRecorder();
  ui.Canvas(recorder).drawRect(ui.Rect.fromLTWH(0, 0, width.toDouble(), height.toDouble()), ui.Paint());
  return recorder.endRecording().toImage(width, height);
}

Future<RemoteControlTileImage> tile({required int x, required int y, required int width, required int height, required int sequence}) async =>
    RemoteControlTileImage(image: await blankImage(width, height), x: x, y: y, width: width, height: height, sequence: sequence);

int tileKey(int x, int y) => (x << 16) | y;

RemoteControlBloc newBloc() {
  final repository = UnusedRemoteControlRepository();
  return RemoteControlBloc(
    requestSession: RequestRemoteControlSession(repository),
    getSession: GetRemoteControlSession(repository),
    endSession: EndRemoteControlSession(repository),
    openStream: OpenRemoteControlStream(repository),
  );
}

Future<void> settle(RemoteControlBloc bloc) => Future<void>.delayed(Duration.zero);

void main() {
  test('a changed top-left tile paints over the full frame instead of replacing it', () async {
    // The agent sends the whole image as one tile at (0, 0), then 256px tiles for whatever changed
    // — the first of which is *also* at (0, 0). Keyed by position alone, the cursor passing through
    // that corner threw the whole picture away and left one 256px square on a dark ground.
    final bloc = newBloc();
    bloc.add(const RemoteControlGeometryChanged(geometry));
    bloc.add(RemoteControlTileDecoded(tileKey(0, 0), await tile(x: 0, y: 0, width: 1600, height: 900, sequence: 1)));
    bloc.add(RemoteControlTileDecoded(tileKey(0, 0), await tile(x: 0, y: 0, width: 256, height: 256, sequence: 2)));
    bloc.add(RemoteControlTileDecoded(tileKey(256, 0), await tile(x: 256, y: 0, width: 256, height: 256, sequence: 3)));
    await settle(bloc);

    final tiles = bloc.state.tiles;
    expect(tiles.length, 3);
    expect(tiles[RemoteControlState.fullFrameKey]?.width, 1600);
    expect(tiles[tileKey(0, 0)]?.sequence, 2);
    expect(tiles[tileKey(256, 0)]?.sequence, 3);

    await bloc.close();
  });

  test('a new full frame clears every partial tile and drops any partial older than itself', () async {
    final bloc = newBloc();
    bloc.add(const RemoteControlGeometryChanged(geometry));
    bloc.add(RemoteControlTileDecoded(tileKey(0, 0), await tile(x: 0, y: 0, width: 1600, height: 900, sequence: 1)));
    bloc.add(RemoteControlTileDecoded(tileKey(512, 256), await tile(x: 512, y: 256, width: 256, height: 256, sequence: 2)));
    bloc.add(RemoteControlTileDecoded(tileKey(0, 0), await tile(x: 0, y: 0, width: 1600, height: 900, sequence: 4)));
    // Encoded before the second full frame but decoded after it: stale pixels, not an update.
    bloc.add(RemoteControlTileDecoded(tileKey(768, 0), await tile(x: 768, y: 0, width: 256, height: 256, sequence: 3)));
    await settle(bloc);

    final tiles = bloc.state.tiles;
    expect(tiles.keys, [RemoteControlState.fullFrameKey]);
    expect(tiles[RemoteControlState.fullFrameKey]?.sequence, 4);

    await bloc.close();
  });

  test('an older tile for a position never repaints over a newer one', () async {
    final bloc = newBloc();
    bloc.add(const RemoteControlGeometryChanged(geometry));
    bloc.add(RemoteControlTileDecoded(tileKey(256, 256), await tile(x: 256, y: 256, width: 256, height: 256, sequence: 7)));
    bloc.add(RemoteControlTileDecoded(tileKey(256, 256), await tile(x: 256, y: 256, width: 256, height: 256, sequence: 6)));
    await settle(bloc);

    expect(bloc.state.tiles[tileKey(256, 256)]?.sequence, 7);

    await bloc.close();
  });
}
