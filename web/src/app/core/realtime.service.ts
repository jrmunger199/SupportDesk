import { HttpClient } from '@angular/common/http';
import { Injectable, effect, inject, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from './auth.service';
import { MentionItem, TicketChange } from './models';

export type ConnectionStatus = 'offline' | 'connecting' | 'live' | 'reconnecting';

/**
 * Keeps one SignalR connection open while a user is signed in and republishes ticket changes as an observable.
 * Components subscribe to `changes$` and decide for themselves what to refresh.
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);
  private readonly http = inject(HttpClient);
  private readonly subject = new Subject<TicketChange>();
  private readonly mentionSubject = new Subject<MentionItem>();
  private connection: HubConnection | null = null;

  /** Tickets the signed-in user changed themselves very recently, so the UI can skip echoing them back. */
  private readonly own = new Map<string, number>();

  readonly status = signal<ConnectionStatus>('offline');
  readonly changes$ = this.subject.asObservable();

  /** Pushed only to the person who was mentioned. */
  readonly mentions$ = this.mentionSubject.asObservable();

  constructor() {
    effect(() => {
      if (this.auth.isAuthenticated()) {
        void this.start();
      } else {
        void this.stop();
      }
    });
  }

  /** Call after a local write; the matching push shortly afterwards is then recognised as our own. */
  markOwnChange(ticketId: string): void {
    this.own.set(ticketId, Date.now());
  }

  isOwnChange(ticketId: string, windowMs = 3000): boolean {
    const at = this.own.get(ticketId);
    return at !== undefined && Date.now() - at < windowMs;
  }

  private async start(): Promise<void> {
    if (this.connection) {
      return;
    }

    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/tickets', { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('ticketChanged', (change: TicketChange) => this.subject.next(change));
    connection.on('mentioned', (mention: MentionItem) => this.mentionSubject.next(mention));
    connection.onreconnecting(() => this.status.set('reconnecting'));
    connection.onreconnected(() => this.status.set('live'));
    connection.onclose(() => {
      this.status.set('offline');
      this.revalidateSession();
    });

    this.connection = connection;
    this.status.set('connecting');

    try {
      await connection.start();
      this.status.set(connection.state === HubConnectionState.Connected ? 'live' : 'offline');
    } catch {
      // The initial attempt failed (server down, token rejected). Stay offline; the page still works via plain HTTP.
      this.status.set('offline');
      this.connection = null;
    }
  }

  /**
   * The server closes a socket when the account is deactivated or the token expires. Ask the API whether the session
   * is still good: a 401 makes the auth interceptor sign the user out, instead of leaving them on a page that
   * silently stopped updating.
   */
  private revalidateSession(): void {
    if (this.auth.isAuthenticated()) {
      this.http.get('/api/auth/me').subscribe({ error: () => undefined });
    }
  }

  private async stop(): Promise<void> {
    const connection = this.connection;
    this.connection = null;
    this.status.set('offline');
    await connection?.stop();
  }
}
