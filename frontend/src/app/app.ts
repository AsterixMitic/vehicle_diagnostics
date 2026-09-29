import { ChangeDetectionStrategy, Component } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatToolbarModule } from '@angular/material/toolbar';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

interface NavItem {
  readonly path: string;
  readonly label: string;
}

@Component({
  selector: 'vd-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatButtonModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App {
  protected readonly nav: readonly NavItem[] = [
    { path: '/live', label: 'Уживо' },
    { path: '/history', label: 'Историја' },
    { path: '/evaluation', label: 'Евалуација' },
    { path: '/devices', label: 'Уређаји' },
  ];
}
