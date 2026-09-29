import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'vd-history',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1>Историја</h1>`,
})
export class History {}
