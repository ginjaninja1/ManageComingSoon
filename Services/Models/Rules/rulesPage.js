define(['jQuery'], function ($) {
    'use strict';

    // ===================================================================
    // Field metadata — drives which operators are valid for a field and
    // what kind of value control the condition shows.
    // ===================================================================
    var FIELD_METADATA = {
        monitored:                      { type: 'bool' },
        hasFile:                        { type: 'bool' },
        year:                           { type: 'number' },
        runtime:                        { type: 'number' },
        tmdbId:                         { type: 'number' },
        imdbId:                         { type: 'string' },
        title:                          { type: 'string' },
        originalTitle:                  { type: 'string' },
        overview:                       { type: 'string' },
        certification:                  { type: 'string' },
        titleSlug:                      { type: 'string' },
        genres:                         { type: 'list' },
        'studios.title':                { type: 'list' },
        'images.coverType':             { type: 'list' },
        'ratings.imdb.value':           { type: 'number' },
        'ratings.tmdb.value':           { type: 'number' },
        'ratings.rottenTomatoes.value': { type: 'number' },
        'ratings.metacritic.value':     { type: 'number' },
        'ratings.imdb.votes':           { type: 'number' },
        'ratings.tmdb.votes':           { type: 'number' }
    };

    var FIELD_OPTIONS = Object.keys(FIELD_METADATA);

    var OPERATORS_BY_TYPE = {
        bool:   ['EQ'],
        number: ['LT', 'LTE', 'GT', 'GTE', 'EQ', 'NEQ'],
        string: ['EQ', 'NEQ', 'CONTAINS', 'NOTCONTAINS'],
        list:   ['CONTAINS', 'NOTCONTAINS']
    };

    var ALL_OPERATORS = ['EQ', 'NEQ', 'LT', 'LTE', 'GT', 'GTE', 'CONTAINS', 'NOTCONTAINS'];

    function fieldType(fieldName) {
        var meta = FIELD_METADATA[fieldName];
        return meta ? meta.type : 'string';
    }

    function operatorAllowedForField(fieldName, operator) {
        var allowed = OPERATORS_BY_TYPE[fieldType(fieldName)];
        return !allowed || allowed.indexOf(operator) !== -1;
    }

    // ===================================================================
    // Pointer-based drag engine. Replaces native HTML5 drag-and-drop
    // entirely — after repeated, environment-specific failures with
    // dataTransfer/dropEffect/dragover in this Emby webview (universal
    // "no entry" cursor, drops silently rejected in some containers but
    // not others), everything here is driven by plain pointerdown /
    // pointermove / pointerup, with a floating "ghost" element that
    // follows the cursor and a registry of drop targets resolved via
    // document.elementFromPoint(). No dataTransfer object is used at all,
    // so none of those browser-specific quirks can recur.
    // ===================================================================
    var dropTargetRegistry = [];
    var activeDrag = null; // { kind, value, reorderElement, ghostEl }
    var highlightedTarget = null;

    function resetDragEngine() {
        dropTargetRegistry = [];
        activeDrag = null;
        highlightedTarget = null;
    }

    function registerDropTarget(el, kinds, onDrop, highlightClass) {
        dropTargetRegistry.push({
            el: el,
            kinds: kinds,
            onDrop: onDrop,
            highlightClass: highlightClass || 'rcsDragOver'
        });
    }

    // Wires a pointerdown source that starts a drag of the given kind.
    // valueFn/reorderElFn may be plain values or functions (evaluated at
    // drag start, so e.g. a chip's current value can be read live).
    function makeDraggableSource(el, kind, valueFn, reorderElFn) {
        el.style.touchAction = 'none'; // stop touch scroll from hijacking the gesture
        el.addEventListener('pointerdown', function (e) {
            if (e.button !== 0 && e.pointerType === 'mouse') return;
            e.preventDefault();
            var value = typeof valueFn === 'function' ? valueFn() : (valueFn || '');
            var reorderEl = typeof reorderElFn === 'function' ? reorderElFn() : (reorderElFn || null);
            startPointerDrag(e, kind, value, reorderEl, el);
        });
    }

    function startPointerDrag(e, kind, value, reorderElement, sourceEl) {
        var ghost = document.createElement('div');
        ghost.className = 'rcsDragGhost';
        ghost.innerText = value || (sourceEl ? sourceEl.innerText : kind);
        document.body.appendChild(ghost);

        activeDrag = { kind: kind, value: value, reorderElement: reorderElement, ghostEl: ghost };
        positionGhost(e.clientX, e.clientY);

        document.addEventListener('pointermove', onPointerMove);
        document.addEventListener('pointerup', onPointerUp);
        document.addEventListener('pointercancel', onPointerCancel);
    }

    function positionGhost(x, y) {
        if (!activeDrag) return;
        activeDrag.ghostEl.style.left = (x + 14) + 'px';
        activeDrag.ghostEl.style.top = (y + 14) + 'px';
    }

    // Insertion-line indicator — a single reusable element shown while
    // hovering a children-container drop target (reorder / new-condition
    // / new-group), positioned at the exact gap the item would land in.
    // Without this, a highlighted container only tells you "somewhere in
    // here", not "between these two specific items".
    var insertionIndicatorEl = null;

    function ensureInsertionIndicator() {
        if (!insertionIndicatorEl) {
            insertionIndicatorEl = document.createElement('div');
            insertionIndicatorEl.className = 'rcsInsertionIndicator';
            document.body.appendChild(insertionIndicatorEl);
        }
        return insertionIndicatorEl;
    }

    function hideInsertionIndicator() {
        if (insertionIndicatorEl) insertionIndicatorEl.style.display = 'none';
    }

    function showInsertionIndicatorAt(containerEl, clientY) {
        var insertBeforeEl = findInsertionPoint(containerEl, clientY);
        var y;

        if (insertBeforeEl) {
            y = insertBeforeEl.getBoundingClientRect().top;
        } else {
            var items = Array.prototype.filter.call(containerEl.children, function (el) {
                return el.classList.contains('rcsCondition') || el.classList.contains('rcsGroup');
            });
            if (items.length) {
                y = items[items.length - 1].getBoundingClientRect().bottom;
            } else {
                y = containerEl.getBoundingClientRect().top + 8;
            }
        }

        var containerRect = containerEl.getBoundingClientRect();
        var indicator = ensureInsertionIndicator();
        indicator.style.display = 'block';
        indicator.style.left = containerRect.left + 'px';
        indicator.style.width = containerRect.width + 'px';
        indicator.style.top = (y - 2) + 'px';
    }

    function findDropTarget(x, y) {
        if (!activeDrag) return null;
        var elAtPoint = document.elementFromPoint(x, y);
        if (!elAtPoint) return null;

        var matches = dropTargetRegistry.filter(function (reg) {
            return reg.kinds.indexOf(activeDrag.kind) !== -1 &&
                (reg.el === elAtPoint || reg.el.contains(elAtPoint));
        });

        if (matches.length === 0) return null;
        if (matches.length === 1) return matches[0];

        // Pick the most deeply nested match — i.e. the one that does NOT
        // itself contain any other match. (Previous version had this
        // backwards: it disqualified a match if some OTHER match
        // contained it, which — since an ancestor always contains its
        // descendant — meant the outermost/root container always won.
        // That was the root cause of drops always landing in the root
        // group instead of a nested one: hovering anywhere inside a
        // nested group's children container also satisfies "root
        // contains this point", and the old logic preferred root.)
        for (var i = 0; i < matches.length; i++) {
            var isMostNested = true;
            for (var j = 0; j < matches.length; j++) {
                if (i !== j && matches[i].el !== matches[j].el && matches[i].el.contains(matches[j].el)) {
                    isMostNested = false;
                    break;
                }
            }
            if (isMostNested) return matches[i];
        }
        return matches[0];
    }

    function onPointerMove(e) {
        if (!activeDrag) return;
        positionGhost(e.clientX, e.clientY);

        var target = findDropTarget(e.clientX, e.clientY);

        if (highlightedTarget && highlightedTarget !== target) {
            highlightedTarget.el.classList.remove(highlightedTarget.highlightClass);
            highlightedTarget = null;
        }
        if (target) {
            target.el.classList.add(target.highlightClass);
            highlightedTarget = target;
        }

        // Only container-type targets (children lists) get an insertion
        // line — slots and badges are single-item drop points where a
        // highlight alone is unambiguous.
        if (target && target.el.classList.contains('rcsGroupChildren')) {
            showInsertionIndicatorAt(target.el, e.clientY);
        } else {
            hideInsertionIndicator();
        }
    }

    function onPointerUp(e) {
        if (!activeDrag) return;

        var target = findDropTarget(e.clientX, e.clientY);
        var drag = activeDrag;

        teardownDrag();

        if (target) {
            target.onDrop(drag.value, drag.reorderElement, e.clientY, e.clientX);
        }
    }

    function onPointerCancel() {
        teardownDrag();
    }

    function teardownDrag() {
        if (highlightedTarget) {
            highlightedTarget.el.classList.remove(highlightedTarget.highlightClass);
            highlightedTarget = null;
        }
        hideInsertionIndicator();
        if (activeDrag && activeDrag.ghostEl && activeDrag.ghostEl.parentNode) {
            activeDrag.ghostEl.parentNode.removeChild(activeDrag.ghostEl);
        }
        activeDrag = null;
        document.removeEventListener('pointermove', onPointerMove);
        document.removeEventListener('pointerup', onPointerUp);
        document.removeEventListener('pointercancel', onPointerCancel);
    }

    // ===================================================================
    // Palette construction
    // ===================================================================
    function populatePalette(view) {
        var fieldContainer = view.querySelector('#rcsFieldChips');
        fieldContainer.innerHTML = '';
        FIELD_OPTIONS.forEach(function (f) {
            fieldContainer.appendChild(makeFieldChip(f));
        });

        var opContainer = view.querySelector('#rcsOperatorChips');
        opContainer.innerHTML = '';
        ALL_OPERATORS.forEach(function (o) {
            opContainer.appendChild(makeOperatorChip(o));
        });
    }

    function makeFieldChip(fieldName) {
        var chip = document.createElement('span');
        chip.className = 'rcsChip rcsChip-field';
        chip.innerText = fieldName;

        var tag = document.createElement('span');
        tag.className = 'rcsFieldTypeTag';
        tag.innerText = '(' + fieldType(fieldName) + ')';
        chip.appendChild(tag);

        makeDraggableSource(chip, 'field', fieldName);
        return chip;
    }

    function makeOperatorChip(operator) {
        var chip = document.createElement('span');
        chip.className = 'rcsChip rcsChip-operator';
        chip.innerText = operator;
        makeDraggableSource(chip, 'operator', operator);
        return chip;
    }

    // Palette chips declared inline in HTML (Condition, Group, AND, OR, NOT)
    function wireStaticPaletteChips(view) {
        view.querySelectorAll('#rcsPalette .rcsChip[data-chip-kind]').forEach(function (chip) {
            var kind = chip.dataset.chipKind;
            var value = chip.dataset.chipValue || '';
            makeDraggableSource(chip, kind, value);
        });
    }

    // ===================================================================
    // Badge helpers (NOT toggle, group connector)
    // ===================================================================
    function makeNotBadge(active, onChange) {
        var badge = document.createElement('span');
        badge.className = 'rcsBadge rcsBadge-not' + (active ? ' rcsBadge-not-active' : ' rcsBadge-not-empty');
        badge.dataset.notActive = active ? 'true' : 'false';
        badge.innerText = active ? 'NOT ✕' : '¬';
        badge.title = 'Drag NOT here to negate; click an active NOT to remove it';

        registerDropTarget(badge, ['not'], function () {
            setNotBadgeActive(badge, true);
            if (onChange) onChange();
        });

        badge.addEventListener('click', function () {
            if (badge.dataset.notActive === 'true') {
                setNotBadgeActive(badge, false);
                if (onChange) onChange();
            }
        });

        return badge;
    }

    function setNotBadgeActive(badge, active) {
        badge.dataset.notActive = active ? 'true' : 'false';
        badge.classList.toggle('rcsBadge-not-active', active);
        badge.classList.toggle('rcsBadge-not-empty', !active);
        badge.innerText = active ? 'NOT ✕' : '¬';
    }

    function makeConnectorBadge(initialValue, onChange) {
        var badge = document.createElement('span');
        badge.className = 'rcsBadge rcsBadge-connector';
        badge.dataset.value = initialValue || 'And';
        badge.innerText = badge.dataset.value === 'Or' ? 'OR' : 'AND';
        badge.title = 'Drag AND / OR here to change how children combine';

        registerDropTarget(badge, ['logic'], function (value) {
            badge.dataset.value = value;
            badge.innerText = value === 'Or' ? 'OR' : 'AND';
            if (onChange) onChange();
        });

        return badge;
    }

    // ===================================================================
    // Value widget — shape depends on the field's type.
    // ===================================================================
    function buildValueWidget(type, initialValue, onChange) {
        var widget = document.createElement('span');
        widget.className = 'rcsValueWidget';
        widget.dataset.value = initialValue || '';

        if (type === 'bool') {
            var toggle = document.createElement('span');
            toggle.className = 'rcsBoolToggle';

            var trueOpt = document.createElement('span');
            trueOpt.className = 'rcsBoolOption';
            trueOpt.innerText = 'True';

            var falseOpt = document.createElement('span');
            falseOpt.className = 'rcsBoolOption';
            falseOpt.innerText = 'False';

            function setActive(val) {
                widget.dataset.value = val;
                trueOpt.classList.toggle('rcsBoolOption-active', val === 'true');
                falseOpt.classList.toggle('rcsBoolOption-active', val === 'false');
            }

            trueOpt.addEventListener('click', function () { setActive('true'); if (onChange) onChange(); });
            falseOpt.addEventListener('click', function () { setActive('false'); if (onChange) onChange(); });

            setActive(initialValue === 'false' ? 'false' : (initialValue === 'true' ? 'true' : ''));

            toggle.appendChild(trueOpt);
            toggle.appendChild(falseOpt);
            widget.appendChild(toggle);
        } else {
            var input = document.createElement('input');
            input.setAttribute('is', 'emby-input');
            input.className = 'rcsValueInput';
            input.type = (type === 'number') ? 'number' : 'text';
            input.placeholder = type === 'list' ? 'value to match in list…' : 'value…';
            input.value = initialValue || '';

            input.addEventListener('input', function () {
                widget.dataset.value = input.value;
                if (onChange) onChange();
            });

            widget.appendChild(input);
        }

        return widget;
    }

    // ===================================================================
    // Condition node
    // ===================================================================
    function buildConditionNode(data, onChange) {
        data = data || {};

        var node = document.createElement('div');
        node.className = 'rcsCondition';
        node.dataset.kind = 'Condition';

        var handle = document.createElement('span');
        handle.className = 'rcsHandle';
        handle.innerHTML = '&#9776;';
        makeDraggableSource(handle, 'reorder', '', function () { return node; });

        var fieldSlot = document.createElement('span');
        fieldSlot.className = 'rcsSlot rcsSlot-field';
        fieldSlot.dataset.slotType = 'field';
        fieldSlot.dataset.value = data.Field || '';
        fieldSlot.innerText = data.Field || 'field…';
        if (data.Field) fieldSlot.classList.add('rcsSlot-filled');

        var operatorSlot = document.createElement('span');
        operatorSlot.className = 'rcsSlot rcsSlot-operator';
        operatorSlot.dataset.slotType = 'operator';
        operatorSlot.dataset.value = data.Operator || '';
        operatorSlot.innerText = data.Operator || 'op…';
        if (data.Operator) operatorSlot.classList.add('rcsSlot-filled');

        var valueHolder = document.createElement('span');
        valueHolder.className = 'rcsValueHolder';

        function currentType() {
            return fieldSlot.dataset.value ? fieldType(fieldSlot.dataset.value) : 'string';
        }

        function rebuildValueWidget() {
            valueHolder.innerHTML = '';
            var widget = buildValueWidget(currentType(), '', onChange);
            valueHolder.appendChild(widget);
        }

        function refreshOperatorLock() {
            var type = currentType();

            if (type === 'bool') {
                operatorSlot.dataset.value = 'EQ';
                operatorSlot.innerText = 'EQ';
                operatorSlot.classList.add('rcsSlot-filled', 'rcsSlot-locked');
            } else {
                operatorSlot.classList.remove('rcsSlot-locked');
                if (operatorSlot.dataset.value && !operatorAllowedForField(fieldSlot.dataset.value, operatorSlot.dataset.value)) {
                    operatorSlot.dataset.value = '';
                    operatorSlot.innerText = 'op…';
                    operatorSlot.classList.remove('rcsSlot-filled');
                }
            }
        }

        registerDropTarget(fieldSlot, ['field'], function (value) {
            fieldSlot.dataset.value = value;
            fieldSlot.innerText = value;
            fieldSlot.classList.add('rcsSlot-filled');
            refreshOperatorLock();
            rebuildValueWidget();
            if (onChange) onChange();
        });

        registerDropTarget(operatorSlot, ['operator'], function (value) {
            if (fieldSlot.dataset.value && !operatorAllowedForField(fieldSlot.dataset.value, value)) {
                operatorSlot.classList.add('rcsSlotRejected');
                setTimeout(function () { operatorSlot.classList.remove('rcsSlotRejected'); }, 500);
                return;
            }
            operatorSlot.dataset.value = value;
            operatorSlot.innerText = value;
            operatorSlot.classList.add('rcsSlot-filled');
            if (onChange) onChange();
        });

        rebuildValueWidget();
        refreshOperatorLock();
        // Preserve any initial Value passed in (rebuildValueWidget above
        // clears it, since on first build there's nothing to preserve
        // from user interaction — restore it explicitly here for loads).
        if (data.Value) {
            var initialWidget = buildValueWidget(currentType(), data.Value, onChange);
            valueHolder.innerHTML = '';
            valueHolder.appendChild(initialWidget);
        }

        var notBadge = makeNotBadge(!!data.Not, onChange);

        var removeBtn = document.createElement('span');
        removeBtn.className = 'rcsIconBtn';
        removeBtn.innerText = '✕';
        removeBtn.title = 'Remove condition';
        removeBtn.addEventListener('click', function () {
            node.parentNode.removeChild(node);
            if (onChange) onChange();
        });

        node.appendChild(handle);
        node.appendChild(fieldSlot);
        node.appendChild(operatorSlot);
        node.appendChild(valueHolder);
        node.appendChild(notBadge);
        node.appendChild(removeBtn);

        return node;
    }

    // ===================================================================
    // Group node (recursive)
    // ===================================================================
    function buildGroupNode(data, isRoot, onChange) {
        data = data || {};

        var group = document.createElement('div');
        group.className = 'rcsGroup' + (isRoot ? ' rcsGroupRoot' : '');
        group.dataset.kind = 'Group';

        var header = document.createElement('div');
        header.className = 'rcsGroupHeader';

        if (!isRoot) {
            var handle = document.createElement('span');
            handle.className = 'rcsHandle';
            handle.innerHTML = '&#9776;';
            makeDraggableSource(handle, 'reorder', '', function () { return group; });
            header.appendChild(handle);
        }

        var label = document.createElement('span');
        label.innerText = isRoot ? 'Root group —' : 'Group —';
        label.style.opacity = '0.6';
        label.style.fontSize = '0.85em';
        header.appendChild(label);

        var connectorBadge = makeConnectorBadge(data.LogicOperator || 'And', onChange);
        header.appendChild(connectorBadge);

        var notBadge = makeNotBadge(!!data.Not, onChange);
        header.appendChild(notBadge);

        if (!isRoot) {
            var removeBtn = document.createElement('span');
            removeBtn.className = 'rcsIconBtn';
            removeBtn.innerText = '✕ Remove group';
            removeBtn.addEventListener('click', function () {
                group.parentNode.removeChild(group);
                if (onChange) onChange();
            });
            header.appendChild(removeBtn);
        }

        group.appendChild(header);

        var childrenContainer = document.createElement('div');
        childrenContainer.className = 'rcsGroupChildren';

        var emptyHint = document.createElement('div');
        emptyHint.className = 'rcsEmptyHint';
        emptyHint.innerText = 'Drag "Condition" or "Group ( )" here';
        childrenContainer.appendChild(emptyHint);

        function refreshEmptyHint() {
            var hasChildren = !!childrenContainer.querySelector('.rcsCondition, .rcsGroup');
            emptyHint.style.display = hasChildren ? 'none' : '';
        }

        (data.Children || []).forEach(function (child) {
            if (child.Kind === 'Group') {
                childrenContainer.appendChild(buildGroupNode(child, false, onChange));
            } else {
                childrenContainer.appendChild(buildConditionNode(child, onChange));
            }
        });
        refreshEmptyHint();

        registerDropTarget(childrenContainer, ['reorder'], function (value, reorderEl, clientY) {
            if (!reorderEl) return;
            var insertBeforeEl = findInsertionPoint(childrenContainer, clientY);
            childrenContainer.insertBefore(reorderEl, insertBeforeEl);
            refreshEmptyHint();
            if (onChange) onChange();
        });

        registerDropTarget(childrenContainer, ['new-condition'], function (value, reorderEl, clientY) {
            var insertBeforeEl = findInsertionPoint(childrenContainer, clientY);
            childrenContainer.insertBefore(buildConditionNode({}, onChange), insertBeforeEl);
            refreshEmptyHint();
            if (onChange) onChange();
        });

        registerDropTarget(childrenContainer, ['new-group'], function (value, reorderEl, clientY) {
            var insertBeforeEl = findInsertionPoint(childrenContainer, clientY);
            childrenContainer.insertBefore(buildGroupNode({}, false, onChange), insertBeforeEl);
            refreshEmptyHint();
            if (onChange) onChange();
        });

        group.appendChild(childrenContainer);

        var footer = document.createElement('div');
        footer.className = 'rcsGroupFooter';
        var hint = document.createElement('span');
        hint.className = 'rcsEmptyHint';
        hint.innerText = '(drop palette items anywhere in this box)';
        footer.appendChild(hint);
        group.appendChild(footer);

        return group;
    }

    function findInsertionPoint(container, clientY) {
        var items = Array.prototype.filter.call(container.children, function (el) {
            return el.classList.contains('rcsCondition') || el.classList.contains('rcsGroup');
        });

        for (var i = 0; i < items.length; i++) {
            var rect = items[i].getBoundingClientRect();
            var midpoint = rect.top + rect.height / 2;
            if (clientY < midpoint) {
                return items[i];
            }
        }

        return null; // append at end
    }

    // ===================================================================
    // Reading the tree back out of the DOM (recursive)
    // ===================================================================
    function readGroupFromDom(groupEl) {
        var childrenContainer = groupEl.querySelector(':scope > .rcsGroupChildren');
        var header = groupEl.querySelector(':scope > .rcsGroupHeader');
        var connectorBadge = header.querySelector('.rcsBadge-connector');
        var notBadge = header.querySelector('.rcsBadge-not');

        var children = [];
        Array.prototype.forEach.call(childrenContainer.children, function (childEl) {
            if (childEl.classList.contains('rcsGroup')) {
                children.push(readGroupFromDom(childEl));
            } else if (childEl.classList.contains('rcsCondition')) {
                children.push(readConditionFromDom(childEl));
            }
        });

        return {
            Kind: 'Group',
            LogicOperator: connectorBadge.dataset.value || 'And',
            Not: notBadge.dataset.notActive === 'true',
            Children: children
        };
    }

    function readConditionFromDom(nodeEl) {
        var fieldSlot = nodeEl.querySelector('.rcsSlot-field');
        var operatorSlot = nodeEl.querySelector('.rcsSlot-operator');
        var valueWidget = nodeEl.querySelector('.rcsValueWidget');
        var notBadge = nodeEl.querySelector('.rcsBadge-not');

        return {
            Kind: 'Condition',
            Field: fieldSlot.dataset.value || '',
            Operator: operatorSlot.dataset.value || '',
            Value: (valueWidget && valueWidget.dataset.value) || '',
            Not: notBadge.dataset.notActive === 'true'
        };
    }

    // ===================================================================
    // Validation — enforced (with highlighting) only on Save.
    // ===================================================================
    function findInvalidConditionElements(rootGroupEl) {
        var invalid = [];
        rootGroupEl.querySelectorAll('.rcsCondition').forEach(function (nodeEl) {
            var fieldSlot = nodeEl.querySelector('.rcsSlot-field');
            var operatorSlot = nodeEl.querySelector('.rcsSlot-operator');
            var valueWidget = nodeEl.querySelector('.rcsValueWidget');

            var isValid = !!fieldSlot.dataset.value && !!operatorSlot.dataset.value &&
                valueWidget && valueWidget.dataset.value !== '';

            if (!isValid) invalid.push(nodeEl);
        });
        return invalid;
    }

    // An empty group (no conditions/subgroups) is not "invalid" the way
    // an incomplete condition is — it's syntactically fine — but under
    // AND semantics it's vacuously TRUE (matches every movie), which is
    // rarely what someone intends when a group is just mid-build. Flag it
    // distinctly so it's visible without blocking Save the way a genuinely
    // incomplete condition does.
    function findEmptyGroupElements(rootGroupEl) {
        var empty = [];
        rootGroupEl.querySelectorAll('.rcsGroup').forEach(function (groupEl) {
            var childrenContainer = groupEl.querySelector(':scope > .rcsGroupChildren');
            var hasChildren = !!childrenContainer.querySelector(':scope > .rcsCondition, :scope > .rcsGroup');
            if (!hasChildren) empty.push(groupEl);
        });
        // Root itself matches the .rcsGroup selector too since it carries
        // both classes — include it (an empty root is the same trap).
        return empty;
    }

    function highlightInvalid(rootGroupEl, invalidNodes) {
        rootGroupEl.querySelectorAll('.rcsCondition').forEach(function (nodeEl) {
            nodeEl.classList.remove('rcsInvalid');
        });
        invalidNodes.forEach(function (nodeEl) { nodeEl.classList.add('rcsInvalid'); });
    }

    function highlightEmptyGroups(rootGroupEl, emptyGroupEls) {
        rootGroupEl.querySelectorAll('.rcsGroup').forEach(function (groupEl) {
            groupEl.classList.remove('rcsGroupEmpty');
        });
        emptyGroupEls.forEach(function (groupEl) { groupEl.classList.add('rcsGroupEmpty'); });
    }

    // ===================================================================
    // Load / Save / Preview
    // ===================================================================
    var autoPreviewTimer = null;

    function scheduleAutoPreview(view) {
        if (autoPreviewTimer) clearTimeout(autoPreviewTimer);
        autoPreviewTimer = setTimeout(function () { runAutoPreview(view); }, 450);
    }

    function renderPreviewTable(container, fields, matches) {
        container.innerHTML = '';

        if (!matches || matches.length === 0) {
            container.innerText = 'No matches.';
            return;
        }

        var wrapper = document.createElement('div');
        wrapper.className = 'rcsPreviewTableWrapper';

        var table = document.createElement('table');
        table.className = 'rcsPreviewTable';

        var headerRow = document.createElement('tr');
        var corner = document.createElement('th');
        corner.innerText = 'Movie';
        headerRow.appendChild(corner);
        matches.forEach(function (m) {
            var th = document.createElement('th');
            th.innerText = m.Title;
            headerRow.appendChild(th);
        });
        table.appendChild(headerRow);

        fields.forEach(function (f) {
            var row = document.createElement('tr');
            var label = document.createElement('td');
            label.innerText = f;
            row.appendChild(label);

            matches.forEach(function (m) {
                var td = document.createElement('td');
                var val = m.Values && m.Values[f];
                td.innerText = (val === undefined || val === null || val === '') ? '—' : val;
                row.appendChild(td);
            });

            table.appendChild(row);
        });

        wrapper.appendChild(table);
        container.appendChild(wrapper);
    }

    function runAutoPreview(view) {
        var rootGroupEl = view.querySelector('#conditionsList > .rcsGroupRoot');
        if (!rootGroupEl) return;

        var statusEl = view.querySelector('#rcsPreviewStatus');
        var resultEl = view.querySelector('#previewResult');
        var jsonEl = view.querySelector('#rcsRuleJson');

        var invalid = findInvalidConditionElements(rootGroupEl);
        var emptyGroups = findEmptyGroupElements(rootGroupEl);
        highlightEmptyGroups(rootGroupEl, emptyGroups);

        if (invalid.length > 0) {
            statusEl.innerText = 'Expression incomplete (' + invalid.length + ' condition(s) missing a field, operator, or value) — preview will resume once it\'s valid.';
            resultEl.innerHTML = '';
            if (jsonEl) jsonEl.textContent = '';
            return;
        }

        var candidate = readGroupFromDom(rootGroupEl);

        if (jsonEl) jsonEl.textContent = JSON.stringify(candidate, null, 2);

        var warningText = '';
        if (emptyGroups.length > 0) {
            warningText = ' ⚠ ' + emptyGroups.length + ' empty group(s) outlined in amber — an empty AND-group matches EVERY movie by default, which may widen this rule further than intended.';
        }
        statusEl.innerText = 'Live preview:' + warningText;

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('ManageComingSoon/RadarrRulePreview'),
            data: JSON.stringify({ Rule: candidate }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            var shown = (result.Matches || []).length;
            var countText = shown < result.MatchCount
                ? result.MatchCount + ' match(es) — showing first ' + shown + ':'
                : result.MatchCount + ' match(es):';
            statusEl.innerText = countText + warningText;

            renderPreviewTable(resultEl, result.Fields || [], result.Matches || []);
        });
    }

    function loadRuleSets(view) {
        ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('ManageComingSoon/RadarrRuleSets'),
            dataType: 'json'
        }).then(function (result) {
            var active = null;
            if (result && result.RuleSets && result.RuleSets.length) {
                active = result.RuleSets.filter(function (r) {
                    return r.Id === result.ActiveRuleSetId;
                })[0] || result.RuleSets[0];
            }

            var list = view.querySelector('#conditionsList');
            list.innerHTML = '';

            var onChange = function () { scheduleAutoPreview(view); };

            var rootGroup = (active && active.Root) ? active.Root : { LogicOperator: 'And', Children: [] };
            list.appendChild(buildGroupNode(rootGroup, true, onChange));

            view.currentRuleSetId = active ? active.Id : null;
            view.currentRuleSetName = active ? active.Name : 'Default';

            scheduleAutoPreview(view);
        });
    }

    function saveRuleSets(view) {
        var rootGroupEl = view.querySelector('#conditionsList > .rcsGroupRoot');

        var invalidNodes = findInvalidConditionElements(rootGroupEl);
        highlightInvalid(rootGroupEl, invalidNodes);

        if (invalidNodes.length > 0) {
            Dashboard.alert('Some conditions are incomplete (missing field, operator, or value) — they\'re outlined in red. Fill them in before saving.');
            invalidNodes[0].scrollIntoView({ behavior: 'smooth', block: 'center' });
            return;
        }

        var emptyGroups = findEmptyGroupElements(rootGroupEl);
        highlightEmptyGroups(rootGroupEl, emptyGroups);
        if (emptyGroups.length > 0) {
            var proceed = confirm(
                emptyGroups.length + ' group(s) are empty (outlined in amber). An empty AND-group matches EVERY movie by default — ' +
                'this rule may be wider than intended. Save anyway?'
            );
            if (!proceed) return;
        }

        var root = readGroupFromDom(rootGroupEl);

        var payload = {
            RuleSets: [
                {
                    Id: view.currentRuleSetId || '',
                    Name: view.currentRuleSetName || 'Default',
                    Root: root
                }
            ],
            ActiveRuleSetId: view.currentRuleSetId || ''
        };

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('ManageComingSoon/RadarrRuleSets'),
            data: JSON.stringify({ Payload: payload }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function () {
            Dashboard.alert('Rule saved.');
        });
    }

    // Emby's theme exposes accent color as HSL components
    // (--theme-primary-color-hue/saturation/lightness, confirmed via
    // DevTools) but doesn't appear to expose a discrete "surface
    // background" variable — the page background is likely painted higher
    // up the DOM without a named custom property. Rather than guess one
    // and risk hardcoding a wrong name (the same problem as hardcoding a
    // literal color), the actual resolved background is read directly off
    // the page at runtime and exposed as our own CSS variable. This stays
    // correct across any theme automatically, with no guessing involved.
    function applySurfaceBackgroundVariable(view) {
        var resolved = getComputedStyle(document.body).backgroundColor;

        if (!resolved || resolved === 'rgba(0, 0, 0, 0)' || resolved === 'transparent') {
            resolved = getComputedStyle(document.documentElement).backgroundColor;
        }
        if (!resolved || resolved === 'rgba(0, 0, 0, 0)' || resolved === 'transparent') {
            resolved = '#202028'; // last-resort fallback if nothing paints a background at all
        }

        view.style.setProperty('--rcs-surface-bg', resolved);
    }

    return function (view) {
        view.addEventListener('viewshow', function () {
            resetDragEngine(); // clear any stale targets from a previous visit to this page
            applySurfaceBackgroundVariable(view);

            populatePalette(view);
            wireStaticPaletteChips(view);
            loadRuleSets(view);

            view.querySelector('#btnSave').addEventListener('click', function () {
                saveRuleSets(view);
            });

            var jsonToggle = view.querySelector('#rcsToggleJson');
            var jsonPanel = view.querySelector('#rcsRuleJson');
            jsonToggle.addEventListener('click', function (e) {
                e.preventDefault();
                var showing = jsonPanel.style.display !== 'none';
                jsonPanel.style.display = showing ? 'none' : 'block';
                jsonToggle.innerText = showing ? 'Show rule JSON (for debugging)' : 'Hide rule JSON';
            });
        });
    };
});