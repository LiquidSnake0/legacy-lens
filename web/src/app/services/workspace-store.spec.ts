import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { WorkspaceStore } from './workspace-store';
import { Workspace } from '../models/lens';

describe('WorkspaceStore', () => {
  let http: HttpTestingController;
  let store: WorkspaceStore;

  const billing: Workspace = {
    id: 'w-billing', name: 'Billing', rootPath: '/repos/billing',
    createdAt: '2026-08-02T00:00:00+00:00', chunks: 58,
  };

  const payroll: Workspace = {
    id: 'w-payroll', name: 'Payroll', rootPath: '/repos/payroll',
    createdAt: '2026-08-01T00:00:00+00:00', chunks: 12,
  };

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(WorkspaceStore);
  });

  afterEach(() => http.verify());

  function load(found: Workspace[]) {
    store.refresh();
    http.expectOne((r) => r.url.endsWith('/workspaces')).flush(found);
  }

  it('selects the newest project when there is nothing remembered', () => {
    // The list arrives newest first.
    load([billing, payroll]);

    expect(store.currentId()).toBe('w-billing');
  });

  it('picks up where the last visit left off', () => {
    load([billing, payroll]);
    store.select('w-payroll');

    // A second visit, same browser.
    load([billing, payroll]);

    expect(store.currentId()).toBe('w-payroll');
  });

  it('drops a remembered project that has since been deleted', () => {
    // Left selected it would show an empty panel with nothing to explain it.
    load([billing, payroll]);
    store.select('w-payroll');

    load([billing]);

    expect(store.currentId()).toBe('w-billing');
  });

  it('has no project selected when there are none', () => {
    load([]);

    expect(store.currentId()).toBeNull();
    expect(store.current()).toBeNull();
  });

  it('calls an empty list a first run rather than a failure', () => {
    load([]);

    expect(store.firstRun()).toBe(true);
    expect(store.error()).toBeNull();
  });

  it('does not call an unreachable API a first run', () => {
    // Showing the "point it at some code" form when the API is down would send
    // the reader to fix the wrong thing.
    store.refresh();
    http.expectOne((r) => r.url.endsWith('/workspaces'))
      .flush({ error: 'nope' }, { status: 500, statusText: 'Server Error' });

    expect(store.firstRun()).toBe(false);
    expect(store.error()).not.toBeNull();
  });
});
