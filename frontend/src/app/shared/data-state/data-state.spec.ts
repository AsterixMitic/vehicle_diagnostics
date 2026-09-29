import { HttpErrorResponse } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { DataSource, DataState } from './data-state';

class StubSource implements DataSource {
  readonly loading = signal(true);
  readonly failure = signal<unknown>(undefined);
  readonly value = signal(false);
  isLoading = () => this.loading();
  error = () => this.failure();
  hasValue = () => this.value();
}

@Component({
  imports: [DataState],
  template: `<vd-data-state [source]="source" [empty]="empty()" emptyText="Нема уређаја."
    ><p class="content">подаци</p></vd-data-state
  >`,
})
class Host {
  readonly source = new StubSource();
  readonly empty = signal(false);
}

describe('DataState', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
    const has = (selector: string) =>
      (fixture.nativeElement as HTMLElement).querySelector(selector) !== null;
    return { fixture, host: fixture.componentInstance, text, has };
  }

  it('shows loading until the first value arrives', () => {
    const { text, has } = render();

    expect(text()).toContain('Учитавање');
    expect(has('.content')).toBe(false);
  });

  it('shows the problem detail on failure', async () => {
    const { fixture, host, text, has } = render();
    host.source.failure.set(
      new HttpErrorResponse({
        status: 400,
        error: { detail: "'bucket' is out of range: 3600 buckets exceed the limit of 1000" },
      }),
    );
    host.source.loading.set(false);
    await fixture.whenStable();

    expect(text()).toContain("'bucket' is out of range");
    expect(has('[role="alert"]')).toBe(true);
    expect(has('.content')).toBe(false);
  });

  it('shows the empty text when the value is empty', async () => {
    const { fixture, host, text, has } = render();
    host.source.value.set(true);
    host.source.loading.set(false);
    host.empty.set(true);
    await fixture.whenStable();

    expect(text()).toContain('Нема уређаја.');
    expect(has('.content')).toBe(false);
  });

  it('shows the content once data is there', async () => {
    const { fixture, host, has } = render();
    host.source.value.set(true);
    host.source.loading.set(false);
    await fixture.whenStable();

    expect(has('.content')).toBe(true);
    expect(has('.refresh')).toBe(false);
  });

  it('keeps showing data under a progress bar while it refreshes', async () => {
    const { fixture, host, has } = render();
    host.source.value.set(true);
    host.source.loading.set(true);
    await fixture.whenStable();

    expect(has('.content')).toBe(true);
    expect(has('.refresh')).toBe(true);
  });
});
