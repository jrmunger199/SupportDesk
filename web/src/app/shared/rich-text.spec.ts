import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Avatar } from './avatar';
import { RichText } from './rich-text';

const ME = '3f2a1c4e-0b5d-4c6e-8f7a-9b0c1d2e3f40';
const OTHER = '0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d';

function render(text: string) {
  sessionStorage.setItem(
    'supportdesk.session',
    JSON.stringify({
      token: 't',
      expiresUtc: new Date(Date.now() + 3_600_000).toISOString(),
      organization: 'Acme',
      user: { id: ME, email: 'me@acme.test', displayName: 'Me', role: 'Agent', isActive: true },
    }),
  );
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
  const fixture = TestBed.createComponent(RichText);
  fixture.componentRef.setInput('text', text);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

describe('RichText', () => {
  afterEach(() => sessionStorage.clear());

  it('renders plain text exactly as written, including spacing and line breaks', () => {
    const root = render('First line\n  indented second line');

    expect(root.textContent).toBe('First line\n  indented second line');
  });

  it('shows mentions as chips with their names, keeping the text around them intact', () => {
    const root = render(`Hi @[Priya Nair](${OTHER}), can you look?`);

    expect(root.textContent).toBe('Hi @Priya Nair, can you look?');
    expect(root.querySelectorAll('.mention')).toHaveLength(1);
  });

  it('highlights a mention of the signed-in person differently', () => {
    const root = render(`@[Me](${ME}) and @[Priya Nair](${OTHER})`);

    const [mine, theirs] = [...root.querySelectorAll('.mention')];
    expect(mine.classList.contains('me')).toBe(true);
    expect(theirs.classList.contains('me')).toBe(false);
  });

  it('never turns typed markup into elements', () => {
    const root = render('<img src=x onerror=alert(1)> and <script>alert(2)</script>');

    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('script')).toBeNull();
    expect(root.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('does not allow a mention name to inject markup either', () => {
    const root = render(`@[<b>bold</b>](${OTHER})`);

    expect(root.querySelector('b')).toBeNull();
    expect(root.querySelector('.mention')!.textContent).toBe('@<b>bold</b>');
  });
});

describe('Avatar', () => {
  const avatar = (name: string, size?: 'sm' | 'md') => {
    const fixture = TestBed.createComponent(Avatar);
    fixture.componentRef.setInput('name', name);
    if (size) fixture.componentRef.setInput('size', size);
    fixture.detectChanges();
    return fixture.nativeElement.querySelector('.avatar') as HTMLElement;
  };

  it('shows first and last initials', () => {
    expect(avatar('Priya Nair').textContent).toBe('PN');
    expect(avatar('Marcus Antonio Lee').textContent).toBe('ML');
  });

  it('copes with a single name and with odd input', () => {
    expect(avatar('Cher').textContent).toBe('CH');
    expect(avatar('   ').textContent).toBe('?');
  });

  it('gives the same person the same colour every time, and different people usually different ones', () => {
    const a = avatar('Priya Nair').style.getPropertyValue('--hue');
    const b = avatar('Priya Nair').style.getPropertyValue('--hue');
    const c = avatar('Marcus Lee').style.getPropertyValue('--hue');

    expect(a).toBe(b);
    expect(a).not.toBe(c);
  });

  it('is hidden from assistive technology, since the name is always shown elsewhere', () => {
    expect(avatar('Priya Nair').getAttribute('aria-hidden')).toBe('true');
  });

  it('has a smaller variant', () => {
    expect(avatar('Priya Nair', 'sm').classList.contains('sm')).toBe(true);
  });
});
