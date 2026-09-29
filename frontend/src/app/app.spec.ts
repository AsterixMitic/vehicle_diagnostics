import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { App } from './app';
import { routes } from './app.routes';
import { Devices } from './features/devices/devices';
import { Evaluation } from './features/evaluation/evaluation';
import { History } from './features/history/history';
import { Live } from './features/live/live';

describe('routes', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter(routes)] }));

  // navigateByUrl with a component type fails the test if another component is activated.

  it('starts on the live page', async () => {
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/', Live);

    expect(TestBed.inject(Router).url).toBe('/live');
  });

  it('opens each page by its English path', async () => {
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/history', History);
    await harness.navigateByUrl('/evaluation', Evaluation);
    await harness.navigateByUrl('/devices', Devices);
  });

  it('sends unknown routes to the live page', async () => {
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/no/such/page', Live);

    expect(TestBed.inject(Router).url).toBe('/live');
  });
});

describe('App shell', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter(routes)] }));

  it('marks the current page in the navigation', async () => {
    const fixture = TestBed.createComponent(App);
    await TestBed.inject(Router).navigateByUrl('/evaluation');
    await fixture.whenStable();
    fixture.detectChanges();

    const links = [...(fixture.nativeElement as HTMLElement).querySelectorAll('nav a')];
    expect(links.map((a) => a.textContent?.trim())).toEqual([
      'Уживо',
      'Историја',
      'Евалуација',
      'Уређаји',
    ]);
    expect(links.find((a) => a.getAttribute('aria-current') === 'page')?.textContent?.trim()).toBe(
      'Евалуација',
    );
  });
});
