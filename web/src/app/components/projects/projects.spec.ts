import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Projects } from './projects';
import { WorkspaceStore } from '../../services/workspace-store';
import { Workspace } from '../../models/lens';

/**
 * The form is the first thing anyone who did not write this tool sees, so what
 * matters here is what it sends and what it refuses to keep.
 */
describe('Projects', () => {
  let http: HttpTestingController;

  const billing: Workspace = {
    id: 'w-billing',
    name: 'Billing',
    rootPath: '/repos/billing',
    createdAt: '2026-08-01T00:00:00+00:00',
    chunks: 58,
  };

  beforeEach(async () => {
    localStorage.clear();

    await TestBed.configureTestingModule({
      imports: [Projects],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function build(existing: Workspace[] = []) {
    const store = TestBed.inject(WorkspaceStore);
    store.all.set(existing);
    store.loading.set(false);
    store.currentId.set(existing[0]?.id ?? null);

    const fixture = TestBed.createComponent(Projects);
    fixture.detectChanges();
    return fixture;
  }

  function created(): Workspace {
    return { ...billing, id: 'w-new', name: 'New' };
  }

  it('shows the form on its own when there is no project yet', () => {
    const element = build().nativeElement as HTMLElement;

    expect(element.querySelector('.onboarding.alone')).toBeTruthy();
    expect(element.querySelector('.picker')).toBeNull();
  });

  it('offers no form at all when the API cannot be reached', () => {
    // An unreachable API produces an empty list too, and inviting someone to
    // point the tool at some code then sends them to fix the wrong thing.
    const store = TestBed.inject(WorkspaceStore);
    store.all.set([]);
    store.loading.set(false);
    store.error.set('The API is not responding.');

    const fixture = TestBed.createComponent(Projects);
    fixture.detectChanges();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.onboarding')).toBeNull();
    expect(element.querySelector('.picker')).toBeNull();
  });

  it('shows the picker once there is something to pick', () => {
    const element = build([billing]).nativeElement as HTMLElement;

    expect(element.querySelector('.picker')).toBeTruthy();
    expect(element.querySelector('.onboarding')).toBeNull();
  });

  it('will not create a project with no name', () => {
    const app = build().componentInstance;

    app.form.setValue({ name: '', rootPath: '/repos/x', repositoryUrl: '', token: '' });
    app.submit();

    http.expectNone(() => true);
    expect(app.nameControl.touched).toBe(true);
  });

  it('will not create a folder project with no folder', () => {
    const app = build().componentInstance;

    app.form.setValue({ name: 'Billing', rootPath: '   ', repositoryUrl: '', token: '' });
    app.submit();

    http.expectNone(() => true);
    expect(app.failure()).toContain('folder');
  });

  it('will not create a repository project with no URL', () => {
    const app = build().componentInstance;
    app.choose('repository');

    app.form.setValue({ name: 'Billing', rootPath: '', repositoryUrl: '  ', token: '' });
    app.submit();

    http.expectNone(() => true);
    expect(app.failure()).toContain('repository URL');
  });

  it('sends a folder as a path and nothing else', () => {
    const app = build().componentInstance;

    app.form.setValue({ name: 'Billing', rootPath: '/repos/billing', repositoryUrl: '', token: '' });
    app.submit();

    const request = http.expectOne((r) => r.url.endsWith('/workspaces') && r.method === 'POST');
    expect(request.request.body).toEqual({ name: 'Billing', rootPath: '/repos/billing' });

    request.flush(created());
  });

  it('sends a repository URL with its token', () => {
    const app = build().componentInstance;
    app.choose('repository');

    app.form.setValue({
      name: 'Billing',
      rootPath: '',
      repositoryUrl: 'https://github.com/org/billing.git',
      token: 'ghp_secret',
    });
    app.submit();

    const request = http.expectOne((r) => r.url.endsWith('/workspaces') && r.method === 'POST');
    expect(request.request.body).toEqual({
      name: 'Billing',
      repositoryUrl: 'https://github.com/org/billing.git',
      token: 'ghp_secret',
    });

    request.flush(created());
  });

  it('omits the token entirely for a public repository', () => {
    // An empty string is not the same as no token: the API would treat one as
    // a credential to put in the URL.
    const app = build().componentInstance;
    app.choose('repository');

    app.form.setValue({
      name: 'Billing',
      rootPath: '',
      repositoryUrl: 'https://github.com/org/billing.git',
      token: '   ',
    });
    app.submit();

    const request = http.expectOne((r) => r.url.endsWith('/workspaces') && r.method === 'POST');
    expect(request.request.body.token).toBeUndefined();

    request.flush(created());
  });

  it('keeps no copy of the token once the project is made', () => {
    const app = build().componentInstance;
    app.choose('repository');

    app.form.setValue({
      name: 'Billing',
      rootPath: '',
      repositoryUrl: 'https://github.com/org/billing.git',
      token: 'ghp_secret',
    });
    app.submit();

    http.expectOne((r) => r.url.endsWith('/workspaces')).flush(created());

    expect(app.form.getRawValue().token).toBe('');
    expect(JSON.stringify(localStorage)).not.toContain('ghp_secret');
  });

  it('selects the project it just made', () => {
    const app = build().componentInstance;
    const store = TestBed.inject(WorkspaceStore);

    app.form.setValue({ name: 'New', rootPath: '/repos/new', repositoryUrl: '', token: '' });
    app.submit();
    http.expectOne((r) => r.url.endsWith('/workspaces')).flush(created());

    expect(store.currentId()).toBe('w-new');
  });

  it('says why creating a project failed rather than staying silent', () => {
    const app = build().componentInstance;

    app.form.setValue({ name: 'Billing', rootPath: '/repos/x', repositoryUrl: '', token: '' });
    app.submit();

    http.expectOne((r) => r.url.endsWith('/workspaces')).flush(
      { error: 'Something is already being indexed.' },
      { status: 409, statusText: 'Conflict' },
    );

    expect(app.failure()).toContain('already being indexed');
    expect(app.busy()).toBe(false);
  });

  it('reloads the list after deleting a project', () => {
    const app = build([billing]).componentInstance;

    app.remove(billing);

    http.expectOne((r) => r.method === 'DELETE' && r.url.endsWith('/workspaces/w-billing'))
      .flush(null, { status: 204, statusText: 'No Content' });

    http.expectOne((r) => r.method === 'GET' && r.url.endsWith('/workspaces')).flush([]);

    expect(TestBed.inject(WorkspaceStore).all()).toEqual([]);
  });
});
