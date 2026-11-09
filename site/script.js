/**
 * FlaUI.Mcp — Official Showcase Interactive Engine
 * Provides the interactive dual-pane demo runner and one-click clipboard utility.
 */

(function () {
  'use strict';

  // ==========================================================================
  // 1. One-Click Clipboard Copy System
  // ==========================================================================

  /**
   * Copies text using navigator.clipboard with fallback to textarea + execCommand.
   * @param {string} text - Plain text to copy
   * @returns {Promise<void>}
   */
  function copyTextToClipboard(text) {
    if (navigator.clipboard && window.isSecureContext) {
      return navigator.clipboard.writeText(text).catch(function () {
        return fallbackExecCopy(text);
      });
    }
    return fallbackExecCopy(text);
  }

  /**
   * Fallback copy using a temporary off-screen textarea.
   * @param {string} text
   * @returns {Promise<void>}
   */
  function fallbackExecCopy(text) {
    return new Promise(function (resolve, reject) {
      try {
        var textArea = document.createElement('textarea');
        textArea.value = text;
        textArea.setAttribute('readonly', '');
        textArea.style.position = 'fixed';
        textArea.style.top = '-9999px';
        textArea.style.left = '-9999px';
        textArea.style.opacity = '0';
        document.body.appendChild(textArea);
        textArea.focus();
        textArea.select();

        var successful = document.execCommand('copy');
        document.body.removeChild(textArea);

        if (successful) {
          resolve();
        } else {
          reject(new Error('execCommand copy failed'));
        }
      } catch (err) {
        reject(err);
      }
    });
  }

  /**
   * Initializes all .copy-btn elements on the page.
   */
  function initClipboard() {
    var copyButtons = document.querySelectorAll('.copy-btn');
    if (!copyButtons || copyButtons.length === 0) return;

    copyButtons.forEach(function (button) {
      var revertTimeout = null;
      var textSpan = button.querySelector('span');
      var originalLabel = textSpan ? textSpan.textContent : 'Copy';

      button.addEventListener('click', function (e) {
        e.preventDefault();
        var commandText = button.getAttribute('data-clipboard');
        if (!commandText) return;

        copyTextToClipboard(commandText)
          .then(function () {
            triggerCopiedState();
          })
          .catch(function () {
            // Even if clipboard permissions block reading back, trigger visual feedback on copy attempt
            triggerCopiedState();
          });

        function triggerCopiedState() {
          if (revertTimeout) {
            clearTimeout(revertTimeout);
          }

          button.classList.add('copied');
          if (textSpan) {
            textSpan.textContent = 'Copied! ✔';
          } else {
            button.textContent = 'Copied! ✔';
          }

          revertTimeout = setTimeout(function () {
            button.classList.remove('copied');
            if (textSpan) {
              textSpan.textContent = originalLabel;
            } else {
              button.textContent = originalLabel;
            }
            revertTimeout = null;
          }, 2000);
        }
      });
    });
  }

  // ==========================================================================
  // 2. Interactive Dual-Pane Demo Runner
  // ==========================================================================

  /**
   * Step definitions for the dual-pane demo.
   * Total 5 states: Step 0 (Initial) through Step 4 (Actuation).
   */
  var STEPS = [
    {
      step: 0,
      title: 'FlaUI.Mcp — Supervised Dual-Pane Session [Ready: No Active Lease]',
      logComment: '// Step 0: Agent connected via MCP stdio protocol',
      toolCall: 'session_initialize',
      toolArgs: '{ "client": "Claude Desktop", "protocol": "2024-11-05" }',
      logOutput: '"FlaUI Automation Server v1.0.0 Ready. Standby for queries."',
      badgeText: 'READY',
      badgeClass: 'log-badge ok'
    },
    {
      step: 1,
      title: 'FlaUI.Mcp — Supervised Dual-Pane Session [Target Acquired: PID 10482]',
      logComment: '// Step 1: Discover target window in UIA3 tree',
      toolCall: 'desktop_find_element',
      toolArgs: '{ "name": "Enterprise Admin Console", "controlType": "Window" }',
      logOutput: '"Found HWND=0x0002A1E AutomationId=\'AdminWin\' PID=10482 Rect=[120,80,680,480]"',
      badgeText: 'FOUND',
      badgeClass: 'log-badge ok'
    },
    {
      step: 2,
      title: 'FlaUI.Mcp — Supervised Dual-Pane Session [Shield Engaged: Zero-Leak Redaction]',
      logComment: '// Step 2: Tree inspection & zero-leak perception guarantee',
      toolCall: 'desktop_get_tree',
      toolArgs: '{ "windowId": 10482 }',
      logOutput: '"Found 2 fields: txt_user (\'admin@corp.internal\'), txt_pass <REDACTED: IsPassword=True>"',
      badgeText: 'CENSORED',
      badgeClass: 'log-badge redact'
    },
    {
      step: 3,
      title: 'FlaUI.Mcp — Supervised Dual-Pane Session [Active Lease #L-482: 4m 58s]',
      logComment: '// Step 3: Check operator input lease authorization & guardrails',
      toolCall: 'desktop_check_lease',
      toolArgs: '',
      logOutput: '"Status=GRANTED, Lease=\'#L-482\', Remaining=04m:58s, Mode=Interactive"',
      badgeText: 'GUARDRAIL PASS',
      badgeClass: 'log-badge lease'
    },
    {
      step: 4,
      title: 'FlaUI.Mcp — Supervised Dual-Pane Session [Active Lease #L-482: 4m 57s]',
      logComment: '// Step 4: Supervised actuation on target button',
      toolCall: 'desktop_click',
      toolArgs: '{ "elementId": "btn_submit" }',
      logOutput: '"Invoked Button.ClickPattern with synthetic cursor verification"',
      badgeText: 'ACTUATED',
      badgeClass: 'log-badge ok'
    }
  ];

  /**
   * Injects dynamic helper CSS classes for animations (ripples, flashes, highlights).
   */
  function injectDynamicStyles() {
    if (document.getElementById('flaui-demo-dynamic-styles')) return;

    var style = document.createElement('style');
    style.id = 'flaui-demo-dynamic-styles';
    style.textContent = [
      '/* Dynamic demo animations & state helpers */',
      '.window-highlight {',
      '  border-bottom: 2px solid var(--desktop) !important;',
      '  box-shadow: 0 0 16px rgba(0, 207, 255, 0.7) !important;',
      '  background: #0d2857 !important;',
      '  color: var(--desktop-soft) !important;',
      '  transition: all 0.3s ease;',
      '}',
      '.shield-flash {',
      '  animation: shieldPulse 1.2s infinite ease-in-out !important;',
      '}',
      '@keyframes shieldPulse {',
      '  0%, 100% {',
      '    box-shadow: 0 0 8px rgba(255, 95, 86, 0.4), inset 0 0 6px rgba(255, 95, 86, 0.2);',
      '    border-color: var(--danger);',
      '  }',
      '  50% {',
      '    box-shadow: 0 0 20px rgba(255, 95, 86, 0.95), inset 0 0 12px rgba(255, 95, 86, 0.4);',
      '    border-color: #ff8a84;',
      '  }',
      '}',
      '.click-ripple {',
      '  position: absolute;',
      '  width: 28px;',
      '  height: 28px;',
      '  border-radius: 50%;',
      '  border: 2px solid var(--desktop);',
      '  box-shadow: 0 0 12px var(--desktop);',
      '  pointer-events: none;',
      '  transform: translate(-50%, -50%) scale(0.2);',
      '  animation: clickRippleAnim 0.55s cubic-bezier(0.1, 0.8, 0.3, 1) forwards;',
      '  z-index: 25;',
      '}',
      '@keyframes clickRippleAnim {',
      '  0% {',
      '    transform: translate(-50%, -50%) scale(0.2);',
      '    opacity: 1;',
      '  }',
      '  100% {',
      '    transform: translate(-50%, -50%) scale(2.6);',
      '    opacity: 0;',
      '  }',
      '}',
      '.demo-btn.active {',
      '  background: var(--desktop) !important;',
      '  color: #020713 !important;',
      '  border-color: var(--desktop-soft) !important;',
      '  box-shadow: 0 0 12px rgba(0, 207, 255, 0.5) !important;',
      '}',
      '.log-line.dimmed {',
      '  opacity: 0.3 !important;',
      '  transition: opacity 0.25s ease;',
      '}',
      '@media (prefers-reduced-motion: reduce) {',
      '  .shield-flash { animation: none !important; }',
      '  .click-ripple { animation: none !important; opacity: 0 !important; }',
      '}'
    ].join('\n');
    document.head.appendChild(style);
  }

  /**
   * Initializes the interactive dual-pane demo.
   */
  function initDemo() {
    var agentStream = document.getElementById('agent-stream');
    var demoTitleStatus = document.getElementById('demo-title-status');
    var mockWin = document.getElementById('mock-win');
    var mockTitleBar = mockWin ? mockWin.querySelector('.mock-title-bar') : null;
    var mockBody = mockWin ? mockWin.querySelector('.mock-body') : null;
    var mockInputPass = document.getElementById('mock-input-pass');
    var mockBtnSubmit = document.getElementById('mock-btn-submit');
    var simCursor = document.getElementById('sim-cursor');

    var btnNext = document.getElementById('btn-demo-next');
    var btnAutoplay = document.getElementById('btn-demo-autoplay');
    var btnReset = document.getElementById('btn-demo-reset');

    if (!agentStream || !mockWin) {
      return; // Graceful degradation if demo elements are missing
    }

    injectDynamicStyles();

    // Move simCursor directly under mockBody for reliable coordinate calculations
    if (simCursor && mockBody && simCursor.parentElement !== mockBody) {
      mockBody.appendChild(simCursor);
    }

    // Create a status message container in mockBody if not present
    var mockStatusMsg = document.getElementById('mock-status-msg');
    if (!mockStatusMsg && mockBody) {
      mockStatusMsg = document.createElement('div');
      mockStatusMsg.id = 'mock-status-msg';
      mockStatusMsg.style.fontSize = '0.78rem';
      mockStatusMsg.style.fontFamily = 'var(--font)';
      mockStatusMsg.style.textAlign = 'right';
      mockStatusMsg.style.marginTop = '0.35rem';
      mockStatusMsg.style.minHeight = '1.2rem';
      mockBody.appendChild(mockStatusMsg);
    }

    var currentStep = 0;
    var autoplayInterval = null;
    var activeStepTimeouts = [];

    function clearActiveTimeouts() {
      while (activeStepTimeouts.length > 0) {
        clearTimeout(activeStepTimeouts.pop());
      }
      // Remove any lingering click ripples
      if (mockBody) {
        var ripples = mockBody.querySelectorAll('.click-ripple');
        ripples.forEach(function (r) {
          if (r.parentNode) r.parentNode.removeChild(r);
        });
      }
    }

    function addTimeout(fn, delay) {
      var id = setTimeout(fn, delay);
      activeStepTimeouts.push(id);
      return id;
    }

    /**
     * Checks if user requested reduced motion.
     */
    function isReducedMotion() {
      try {
        return window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
      } catch (e) {
        return false;
      }
    }

    /**
     * Calculates child coordinates relative to a positioned parent element.
     */
    function getRelativeCoords(child, parent) {
      if (!child || !parent) return { x: 0, y: 0, width: 0, height: 0 };
      var parentRect = parent.getBoundingClientRect();
      var childRect = child.getBoundingClientRect();
      return {
        x: childRect.left - parentRect.left,
        y: childRect.top - parentRect.top,
        width: childRect.width,
        height: childRect.height
      };
    }

    /**
     * Builds and populates the stream lines in the left pane.
     */
    function setupStreamLines() {
      agentStream.innerHTML = '';
      STEPS.forEach(function (stepData) {
        var line = document.createElement('div');
        line.className = 'log-line';
        line.id = 'demo-step-' + stepData.step;

        var commentSpan = '<span class="log-comment">' + stepData.logComment + '</span>';
        var callSpan = '<span>&gt; <span class="log-tool">' + stepData.toolCall + '</span>(' + stepData.toolArgs + ')</span>';
        var badgeSpan = '<span class="' + stepData.badgeClass + '">' + stepData.badgeText + '</span>';
        var outSpan = '<span>&lt; <span class="log-str">' + stepData.logOutput + '</span> ' + badgeSpan + '</span>';

        line.innerHTML = commentSpan + '\n' + callSpan + '\n' + outSpan;
        agentStream.appendChild(line);
      });
    }

    setupStreamLines();

    /**
     * Updates the demo UI for a specific step (0 through 4).
     */
    function renderStep(step) {
      clearActiveTimeouts();
      currentStep = step;
      var stepData = STEPS[step];

      // 1. Update Title / Header Status
      if (demoTitleStatus && stepData) {
        demoTitleStatus.textContent = stepData.title;
      }

      // 2. Update Left Log Stream
      var lines = agentStream.querySelectorAll('.log-line');
      lines.forEach(function (line, idx) {
        if (idx === step) {
          line.className = 'log-line active';
          line.style.opacity = '1';
          try {
            line.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
          } catch (e) {
            line.scrollIntoView();
          }
        } else if (idx < step) {
          line.className = 'log-line';
          line.style.opacity = '0.75';
        } else {
          line.className = 'log-line dimmed';
          line.style.opacity = '0.3';
        }
      });

      // 3. Update Right Pane Controls according to step rules
      var reduced = isReducedMotion();

      // Reset common mock controls before applying specific step states
      if (mockTitleBar) {
        mockTitleBar.classList.remove('window-highlight');
      }

      if (mockInputPass) {
        mockInputPass.classList.remove('shield-flash');
      }

      if (mockBtnSubmit) {
        mockBtnSubmit.classList.remove('targeted-elem');
      }

      if (mockStatusMsg) {
        mockStatusMsg.innerHTML = '';
      }

      switch (step) {
        case 0:
          // Step 0: Ready state
          if (mockInputPass) {
            mockInputPass.classList.remove('redacted-field');
            mockInputPass.innerHTML = '<span>••••••••••••</span> <span style="font-size:0.7rem; color:var(--muted); font-family:var(--mono);">[PasswordBox]</span>';
          }
          if (mockBtnSubmit) {
            mockBtnSubmit.innerHTML = 'Authorize &amp; Deploy';
          }
          if (simCursor) {
            simCursor.style.opacity = '0';
            simCursor.style.left = '220px';
            simCursor.style.top = '170px';
            simCursor.style.transform = 'scale(1)';
          }
          break;

        case 1:
          // Step 1: Find Window -> Highlight Title Bar
          if (mockTitleBar) {
            mockTitleBar.classList.add('window-highlight');
          }
          if (mockInputPass) {
            mockInputPass.classList.remove('redacted-field');
            mockInputPass.innerHTML = '<span>••••••••••••</span> <span style="font-size:0.7rem; color:var(--muted); font-family:var(--mono);">[PasswordBox]</span>';
          }
          if (mockBtnSubmit) {
            mockBtnSubmit.innerHTML = 'Authorize &amp; Deploy';
          }
          if (simCursor) {
            simCursor.style.opacity = '0.6';
            simCursor.style.left = '180px';
            simCursor.style.top = '12px';
            simCursor.style.transform = 'scale(1)';
          }
          break;

        case 2:
          // Step 2: Tree Inspection & Redaction -> Red safeguard badge flashes
          if (mockInputPass) {
            mockInputPass.classList.add('redacted-field');
            if (!reduced) {
              mockInputPass.classList.add('shield-flash');
            }
            mockInputPass.innerHTML = '<span>🛡️ [REDACTED: IsPassword=True]</span> <span class="badge badge-danger">SAFEGUARD</span>';
          }
          if (mockBtnSubmit) {
            mockBtnSubmit.innerHTML = 'Authorize &amp; Deploy';
          }
          if (simCursor) {
            simCursor.style.opacity = '0.75';
            simCursor.style.left = '160px';
            simCursor.style.top = '92px';
            simCursor.style.transform = 'scale(1)';
          }
          break;

        case 3:
          // Step 3: Lease Verification -> Guardrail pass
          if (mockInputPass) {
            mockInputPass.classList.add('redacted-field');
            mockInputPass.innerHTML = '<span>🛡️ [REDACTED: IsPassword=True]</span> <span class="badge badge-danger">SAFEGUARD</span>';
          }
          if (mockBtnSubmit) {
            mockBtnSubmit.innerHTML = 'Authorize &amp; Deploy';
          }
          if (simCursor) {
            simCursor.style.opacity = '0.85';
            simCursor.style.left = '80px';
            simCursor.style.top = '145px';
            simCursor.style.transform = 'scale(1)';
          }
          break;

        case 4:
          // Step 4: Synthetic Click Actuation -> Cyan glow pulse, cursor glide, ripple, success status
          if (mockInputPass) {
            mockInputPass.classList.add('redacted-field');
            mockInputPass.innerHTML = '<span>🛡️ [REDACTED: IsPassword=True]</span> <span class="badge badge-danger">SAFEGUARD</span>';
          }

          if (mockBtnSubmit) {
            mockBtnSubmit.classList.add('targeted-elem');
            mockBtnSubmit.innerHTML = '<span class="target-tag">TARGETED: btn_submit</span> Authorize &amp; Deploy';
          }

          if (simCursor && mockBtnSubmit && mockBody) {
            var coords = getRelativeCoords(mockBtnSubmit, mockBody);
            var targetX = coords.x + Math.min(coords.width / 2, 70);
            var targetY = coords.y + (coords.height / 2);

            simCursor.style.opacity = '1';
            simCursor.style.left = targetX + 'px';
            simCursor.style.top = targetY + 'px';

            if (reduced) {
              // Immediate actuation without animated glide / ripple
              if (mockBtnSubmit) {
                mockBtnSubmit.innerHTML = '<span class="target-tag">TARGETED: btn_submit</span> <strong style="color:var(--success)">Transaction Submitted ✔</strong>';
              }
              if (mockStatusMsg) {
                mockStatusMsg.innerHTML = '<strong style="color:var(--success)">Transaction Submitted ✔</strong>';
              }
            } else {
              // Wait for glide transition (450ms) before clicking
              addTimeout(function () {
                // Click down/up pulse
                simCursor.style.transform = 'scale(0.82)';
                addTimeout(function () {
                  simCursor.style.transform = 'scale(1)';
                }, 140);

                // Create visual click ripple
                var ripple = document.createElement('div');
                ripple.className = 'click-ripple';
                ripple.style.left = targetX + 'px';
                ripple.style.top = targetY + 'px';
                mockBody.appendChild(ripple);

                addTimeout(function () {
                  if (ripple.parentNode) {
                    ripple.parentNode.removeChild(ripple);
                  }
                }, 600);

                // Update right pane status
                if (mockBtnSubmit) {
                  mockBtnSubmit.innerHTML = '<span class="target-tag">TARGETED: btn_submit</span> <strong style="color:var(--success)">Transaction Submitted ✔</strong>';
                }
                if (mockStatusMsg) {
                  mockStatusMsg.innerHTML = '<strong style="color:var(--success)">Transaction Submitted ✔</strong>';
                }
              }, 450);
            }
          }
          break;
      }
    }

    /**
     * Advances to the next step, cycling from 4 back to 0.
     */
    function nextStep() {
      var next = (currentStep + 1) % 5;
      renderStep(next);
    }

    /**
     * Resets immediately to Step 0.
     */
    function resetDemo() {
      stopAutoplay();
      renderStep(0);
    }

    /**
     * Starts automated step progression (2.5s interval).
     */
    function startAutoplay() {
      if (autoplayInterval) return;

      if (btnAutoplay) {
        btnAutoplay.textContent = 'Pause ⏸';
        btnAutoplay.classList.add('active');
        btnAutoplay.setAttribute('aria-pressed', 'true');
      }

      autoplayInterval = setInterval(function () {
        nextStep();
      }, 2500);
    }

    /**
     * Stops automated step progression.
     */
    function stopAutoplay() {
      if (!autoplayInterval) return;

      clearInterval(autoplayInterval);
      autoplayInterval = null;

      if (btnAutoplay) {
        btnAutoplay.textContent = 'Auto-Play ⟳';
        btnAutoplay.classList.remove('active');
        btnAutoplay.setAttribute('aria-pressed', 'false');
      }
    }

    /**
     * Toggles autoplay on / off.
     */
    function toggleAutoplay() {
      if (autoplayInterval) {
        stopAutoplay();
      } else {
        startAutoplay();
      }
    }

    // Attach control event listeners
    if (btnNext) {
      btnNext.addEventListener('click', function (e) {
        e.preventDefault();
        // If autoplay is running, restart interval on manual step
        if (autoplayInterval) {
          clearInterval(autoplayInterval);
          autoplayInterval = setInterval(nextStep, 2500);
        }
        nextStep();
      });
    }

    if (btnReset) {
      btnReset.addEventListener('click', function (e) {
        e.preventDefault();
        resetDemo();
      });
    }

    if (btnAutoplay) {
      btnAutoplay.addEventListener('click', function (e) {
        e.preventDefault();
        toggleAutoplay();
      });
    }

    // Handle window resize dynamically adjusting cursor on Step 4
    var resizeTimer = null;
    window.addEventListener('resize', function () {
      if (resizeTimer) clearTimeout(resizeTimer);
      resizeTimer = setTimeout(function () {
        if (currentStep === 4 && simCursor && mockBtnSubmit && mockBody) {
          var coords = getRelativeCoords(mockBtnSubmit, mockBody);
          var targetX = coords.x + Math.min(coords.width / 2, 70);
          var targetY = coords.y + (coords.height / 2);
          simCursor.style.left = targetX + 'px';
          simCursor.style.top = targetY + 'px';
        }
      }, 100);
    });

    // Initialize to Step 0 immediately
    renderStep(0);
  }

  // ==========================================================================
  // 3. Application Lifecycle Bootstrapper
  // ==========================================================================

  function bootstrap() {
    initClipboard();
    initDemo();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', bootstrap);
  } else {
    bootstrap();
  }
})();
