import { Routes } from '@angular/router';
import { adminGuard, authGuard, guestGuard } from './core/guards';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    title: 'Sign in · SupportDesk',
    loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage),
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'tickets' },
      {
        path: 'tickets',
        title: 'Tickets · SupportDesk',
        loadComponent: () =>
          import('./features/tickets/ticket-list.page').then((m) => m.TicketListPage),
      },
      {
        path: 'board',
        title: 'Board · SupportDesk',
        loadComponent: () => import('./features/board/board.page').then((m) => m.BoardPage),
      },
      {
        path: 'tickets/new',
        title: 'New ticket · SupportDesk',
        loadComponent: () =>
          import('./features/tickets/ticket-new.page').then((m) => m.TicketNewPage),
      },
      {
        path: 'tickets/:id',
        title: 'Ticket · SupportDesk',
        loadComponent: () =>
          import('./features/tickets/ticket-detail.page').then((m) => m.TicketDetailPage),
      },
      {
        path: 'admin/users',
        canActivate: [adminGuard],
        title: 'People · SupportDesk',
        loadComponent: () => import('./features/admin/users.page').then((m) => m.UsersPage),
      },
    ],
  },
  { path: '**', redirectTo: '' },
];
