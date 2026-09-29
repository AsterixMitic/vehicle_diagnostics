import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { RangeSelection } from '../../core/time/range';
import { RangePicker } from './range-picker';

@Component({
  imports: [RangePicker],
  template: `<vd-range-picker
    [selection]="selection()"
    (selectionChange)="changes.push($event)"
  />`,
})
class Host {
  readonly selection = signal<RangeSelection>({ kind: 'preset', preset: '15m' });
  readonly changes: RangeSelection[] = [];
}

describe('RangePicker', () => {
  function render() {
    const fixture = TestBed.createComponent(Host);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const toggle = (label: string) =>
      [...root.querySelectorAll('mat-button-toggle button')].find((b) =>
        b.textContent?.includes(label),
      ) as HTMLButtonElement;
    return { fixture, host: fixture.componentInstance, root, toggle };
  }

  it('emits a preset when one is chosen', async () => {
    const { fixture, host, toggle } = render();

    toggle('1 сат').click();
    await fixture.whenStable();

    expect(host.changes).toEqual([{ kind: 'preset', preset: '1h' }]);
  });

  it('asks for explicit bounds and emits them as instants', async () => {
    const { fixture, host, root, toggle } = render();

    toggle('Прилагођено').click();
    await fixture.whenStable();
    fixture.detectChanges();
    const [from, to] = [
      ...root.querySelectorAll('input[type="datetime-local"]'),
    ] as HTMLInputElement[];
    from.value = '2026-09-29T10:00';
    from.dispatchEvent(new Event('input'));
    to.value = '2026-09-29T11:30';
    to.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (root.querySelector('form') as HTMLFormElement).dispatchEvent(new Event('submit'));
    await fixture.whenStable();

    expect(host.changes).toEqual([
      { kind: 'explicit', from: new Date('2026-09-29T10:00'), to: new Date('2026-09-29T11:30') },
    ]);
  });

  it('shows explicit bounds of an explicit selection', async () => {
    const { fixture, host, root } = render();

    host.selection.set({
      kind: 'explicit',
      from: new Date('2026-09-29T08:00'),
      to: new Date('2026-09-29T09:00'),
    });
    await fixture.whenStable();
    fixture.detectChanges();

    const inputs = [...root.querySelectorAll('input[type="datetime-local"]')] as HTMLInputElement[];
    expect(inputs.map((i) => i.value)).toEqual(['2026-09-29T08:00', '2026-09-29T09:00']);
  });
});
