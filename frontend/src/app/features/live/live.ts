import { ChangeDetectionStrategy, Component } from '@angular/core';

@Component({
  selector: 'vd-live',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<h1>Уживо</h1>`,
})
export class Live {}
