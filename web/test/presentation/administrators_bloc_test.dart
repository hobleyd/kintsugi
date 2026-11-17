import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/administrators_bloc.dart';

class _Repository implements AdministratorsRepository {
  _Repository(this.admins, {this.failure});

  List<Administrator> admins;
  final ApiException? failure;

  @override
  Future<List<Administrator>> list() async => failure != null ? throw failure! : admins;

  @override
  Future<List<Administrator>> add(List<String> emails) async {
    if (failure != null) throw failure!;
    admins = [
      ...admins,
      for (final e in emails)
        if (!admins.any((a) => a.email == e)) Administrator(email: e, addedBy: null, addedUtc: null),
    ];
    return admins;
  }

  @override
  Future<List<Administrator>> remove(String email) async {
    if (failure != null) throw failure!;
    admins = admins.where((a) => a.email != email).toList();
    return admins;
  }
}

Administrator _a(String email) => Administrator(email: email, addedBy: null, addedUtc: null);

AdministratorsBloc _bloc(_Repository repository) => AdministratorsBloc(
      getAdministrators: GetAdministrators(repository),
      addAdministrators: AddAdministrators(repository),
      removeAdministrator: RemoveAdministrator(repository),
    );

void main() {
  blocTest<AdministratorsBloc, AdministratorsState>(
    'loads the list',
    build: () => _bloc(_Repository([_a('a@example.com')])),
    act: (bloc) => bloc.add(const AdministratorsRequested()),
    expect: () => [
      const AdministratorsState(),
      AdministratorsState(administrators: [_a('a@example.com')], loading: false),
    ],
  );

  blocTest<AdministratorsBloc, AdministratorsState>(
    'a failed load leaves the list null, so it is not mistaken for an empty one',
    build: () => _bloc(_Repository([], failure: const ApiException('Could not reach the server.'))),
    act: (bloc) => bloc.add(const AdministratorsRequested()),
    verify: (bloc) {
      expect(bloc.state.administrators, isNull);
      expect(bloc.state.error, 'Could not reach the server.');
      expect(bloc.state.loading, isFalse);
    },
  );

  blocTest<AdministratorsBloc, AdministratorsState>(
    'adding reports how many were new',
    build: () => _bloc(_Repository([_a('a@example.com')])),
    seed: () => AdministratorsState(administrators: [_a('a@example.com')], loading: false),
    act: (bloc) => bloc.add(const AdministratorsAddRequested(['a@example.com', 'b@example.com'])),
    verify: (bloc) {
      expect(bloc.state.administrators!.map((a) => a.email), ['a@example.com', 'b@example.com']);
      expect(bloc.state.notice, 'Added 1 administrator.');
      expect(bloc.state.busy, isFalse);
    },
  );

  blocTest<AdministratorsBloc, AdministratorsState>(
    'adding only people already listed says so',
    build: () => _bloc(_Repository([_a('a@example.com')])),
    seed: () => AdministratorsState(administrators: [_a('a@example.com')], loading: false),
    act: (bloc) => bloc.add(const AdministratorsAddRequested(['a@example.com'])),
    verify: (bloc) => expect(bloc.state.notice, 'Already an administrator.'),
  );

  blocTest<AdministratorsBloc, AdministratorsState>(
    'a validation failure is shown as its messages, once each, not its keys',
    build: () => _bloc(_Repository([], failure: const ApiException('Validation failed.', validationErrors: {
      'Emails[0]': ["'x' is not a plain email address."],
      'Emails[1]': ["'x' is not a plain email address."],
    }))),
    act: (bloc) => bloc.add(const AdministratorsAddRequested(['x', 'x'])),
    verify: (bloc) => expect(bloc.state.error, "'x' is not a plain email address."),
  );

  blocTest<AdministratorsBloc, AdministratorsState>(
    'deleting returns the list as it now stands',
    build: () => _bloc(_Repository([_a('a@example.com'), _a('b@example.com')])),
    act: (bloc) => bloc.add(const AdministratorsRemoveRequested('a@example.com')),
    verify: (bloc) {
      expect(bloc.state.administrators!.map((a) => a.email), ['b@example.com']);
      expect(bloc.state.notice, 'Deleted a@example.com.');
    },
  );

  blocTest<AdministratorsBloc, AdministratorsState>(
    'a refused delete keeps the list and shows the reason',
    build: () => _bloc(_Repository([_a('me@example.com')], failure: const ApiException('You would lock yourself out.'))),
    seed: () => AdministratorsState(administrators: [_a('me@example.com')], loading: false),
    act: (bloc) => bloc.add(const AdministratorsRemoveRequested('me@example.com')),
    verify: (bloc) {
      expect(bloc.state.administrators!.map((a) => a.email), ['me@example.com']);
      expect(bloc.state.error, 'You would lock yourself out.');
      expect(bloc.state.busy, isFalse);
    },
  );
}
