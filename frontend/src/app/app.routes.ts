import { Routes } from '@angular/router';

/** English paths, Serbian titles; every page is loaded on first visit (spec). */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'live' },
  {
    path: 'live',
    title: 'Уживо · Дијагностика возила',
    loadComponent: () => import('./features/live/live').then((m) => m.Live),
  },
  {
    path: 'history',
    title: 'Историја · Дијагностика возила',
    loadComponent: () => import('./features/history/history').then((m) => m.History),
  },
  {
    path: 'evaluation',
    title: 'Евалуација · Дијагностика возила',
    loadComponent: () => import('./features/evaluation/evaluation').then((m) => m.Evaluation),
  },
  {
    path: 'devices',
    title: 'Уређаји · Дијагностика возила',
    loadComponent: () => import('./features/devices/devices').then((m) => m.Devices),
  },
  { path: '**', redirectTo: 'live' },
];
