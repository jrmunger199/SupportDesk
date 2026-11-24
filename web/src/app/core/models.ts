// Mirrors the API contracts (src/SupportDesk.Api/Contracts). Enums travel as strings.

export type Role = 'Requester' | 'Agent' | 'Admin';
export type TicketStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed';
export type TicketPriority = 'Low' | 'Normal' | 'High' | 'Urgent';
export type TicketEventType =
  'Created' | 'StatusChanged' | 'PriorityChanged' | 'Assigned' | 'Unassigned';

export const STATUSES: readonly TicketStatus[] = ['Open', 'InProgress', 'Resolved', 'Closed'];
export const PRIORITIES: readonly TicketPriority[] = ['Low', 'Normal', 'High', 'Urgent'];
export const ROLES: readonly Role[] = ['Requester', 'Agent', 'Admin'];

export const STATUS_LABEL: Record<TicketStatus, string> = {
  Open: 'Open',
  InProgress: 'In progress',
  Resolved: 'Resolved',
  Closed: 'Closed',
};

export interface UserDto {
  id: string;
  email: string;
  displayName: string;
  role: Role;
  isActive: boolean;
}

export interface AuthResponse {
  token: string;
  expiresUtc: string;
  user: UserDto;
  organization: string;
}

export interface UserRef {
  id: string;
  displayName: string;
}

export interface TicketSummary {
  id: string;
  number: number;
  title: string;
  status: TicketStatus;
  priority: TicketPriority;
  requester: UserRef;
  assignee: UserRef | null;
  createdUtc: string;
  updatedUtc: string;
  commentCount: number;
}

export interface CommentDto {
  id: string;
  author: UserRef;
  body: string;
  createdUtc: string;
}

export interface TicketEventDto {
  id: string;
  actor: UserRef;
  type: TicketEventType;
  detail: string;
  createdUtc: string;
}

export interface TicketDetail extends Omit<TicketSummary, 'commentCount'> {
  description: string;
  rowVersion: string;
  allowedTransitions: TicketStatus[];
  comments: CommentDto[];
  events: TicketEventDto[];
}

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface TicketStats {
  byStatus: Record<TicketStatus, number>;
  unassigned: number;
  assignedToMe: number;
}

export interface TicketChange {
  kind: 'created' | 'updated' | 'commented';
  ticket: TicketSummary;
}

export interface UpdateTicketRequest {
  status?: TicketStatus;
  priority?: TicketPriority;
  assigneeId?: string;
  clearAssignee: boolean;
  rowVersion: string;
}

export interface TicketFilters {
  search: string;
  status: TicketStatus | '';
  priority: TicketPriority | '';
  assignment: 'all' | 'me' | 'unassigned';
  sort: 'updated' | 'created' | 'priority' | 'number';
  descending: boolean;
  page: number;
}

// ---- Board ----

export interface BoardCard {
  id: string;
  number: number;
  title: string;
  status: TicketStatus;
  priority: TicketPriority;
  requester: UserRef;
  assignee: UserRef | null;
  updatedUtc: string;
  commentCount: number;
  rowVersion: string;
  allowedTransitions: TicketStatus[];
}

export interface BoardColumn {
  status: TicketStatus;
  total: number;
  cards: BoardCard[];
}

export interface Board {
  columns: BoardColumn[];
  canMove: boolean;
}

// ---- Mentions ----

export interface MentionCandidate {
  id: string;
  displayName: string;
  role: Role;
}

export interface MentionItem {
  id: string;
  ticketId: string;
  ticketNumber: number;
  ticketTitle: string;
  actor: UserRef;
  snippet: string;
  createdUtc: string;
  isRead: boolean;
}

export interface MentionInbox {
  items: MentionItem[];
  unreadCount: number;
}
