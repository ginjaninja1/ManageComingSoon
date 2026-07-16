define(['jQuery'], function ($) {
    'use strict';

    var FIELD_OPTIONS = [
        'monitored', 'hasFile', 'year', 'runtime', 'tmdbId', 'imdbId',
        'title', 'originalTitle', 'overview', 'certification', 'titleSlug',
        'genres', 'studios.title', 'images.coverType',
        'ratings.imdb.value', 'ratings.tmdb.value',
        'ratings.rottenTomatoes.value', 'ratings.metacritic.value',
        'ratings.imdb.votes', 'ratings.tmdb.votes'
    ];

    var OPERATOR_OPTIONS = [
        'EQ', 'NEQ', 'LT', 'LTE', 'GT', 'GTE', 'CONTAINS', 'NOTCONTAINS'
    ];

    function buildConditionRow(view, condition) {
        var row = document.createElement('div');
        row.className = 'ruleConditionRow';
        row.style.display = 'flex';
        row.style.gap = '0.5em';
        row.style.marginBottom = '0.5em';

        var fieldSelect = document.createElement('select');
        fieldSelect.setAttribute('is', 'emby-select');
        fieldSelect.className = 'conditionField';
        FIELD_OPTIONS.forEach(function (f) {
            var opt = document.createElement('option');
            opt.value = f;
            opt.text = f;
            fieldSelect.appendChild(opt);
        });
        fieldSelect.value = condition.Field || FIELD_OPTIONS[0];

        var opSelect = document.createElement('select');
        opSelect.setAttribute('is', 'emby-select');
        opSelect.className = 'conditionOperator';
        OPERATOR_OPTIONS.forEach(function (o) {
            var opt = document.createElement('option');
            opt.value = o;
            opt.text = o;
            opSelect.appendChild(opt);
        });
        opSelect.value = condition.Operator || 'EQ';

        var valueInput = document.createElement('input');
        valueInput.setAttribute('is', 'emby-input');
        valueInput.type = 'text';
        valueInput.className = 'conditionValue';
        valueInput.value = condition.Value || '';

        var notLabel = document.createElement('label');
        var notCheckbox = document.createElement('input');
        notCheckbox.type = 'checkbox';
        notCheckbox.className = 'conditionNot';
        notCheckbox.checked = !!condition.Not;
        notLabel.appendChild(notCheckbox);
        notLabel.appendChild(document.createTextNode(' NOT'));

        var removeBtn = document.createElement('button');
        removeBtn.setAttribute('is', 'emby-button');
        removeBtn.type = 'button';
        removeBtn.className = 'removeCondition';
        removeBtn.innerText = 'Remove';
        removeBtn.addEventListener('click', function () {
            row.parentNode.removeChild(row);
        });

        row.appendChild(fieldSelect);
        row.appendChild(opSelect);
        row.appendChild(valueInput);
        row.appendChild(notLabel);
        row.appendChild(removeBtn);

        return row;
    }

    function readConditionsFromDom(view) {
        var rows = view.querySelectorAll('.ruleConditionRow');
        var conditions = [];
        rows.forEach(function (row) {
            conditions.push({
                Kind: 'Condition',
                Field: row.querySelector('.conditionField').value,
                Operator: row.querySelector('.conditionOperator').value,
                Value: row.querySelector('.conditionValue').value,
                Not: row.querySelector('.conditionNot').checked
            });
        });
        return conditions;
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

            if (active && active.Root) {
                view.querySelector('#selectLogicOperator').value = active.Root.LogicOperator || 'And';

                (active.Root.Children || []).forEach(function (child) {
                    list.appendChild(buildConditionRow(view, child));
                });
            }

            view.currentRuleSetId = active ? active.Id : null;
            view.currentRuleSetName = active ? active.Name : 'Default';
        });
    }

    function saveRuleSets(view) {
        var payload = {
            RuleSets: [
                {
                    Id: view.currentRuleSetId || '',
                    Name: view.currentRuleSetName || 'Default',
                    Root: {
                        Kind: 'Group',
                        LogicOperator: view.querySelector('#selectLogicOperator').value,
                        Not: false,
                        Children: readConditionsFromDom(view)
                    }
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

    function previewRule(view) {
        var candidate = {
            Kind: 'Group',
            LogicOperator: view.querySelector('#selectLogicOperator').value,
            Not: false,
            Children: readConditionsFromDom(view)
        };

        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('ManageComingSoon/RadarrRulePreview'),
            data: JSON.stringify({ Rule: candidate }),
            contentType: 'application/json',
            dataType: 'json'
        }).then(function (result) {
            var el = view.querySelector('#previewResult');
            el.innerHTML = '<strong>' + result.MatchCount + ' match(es):</strong><br/>' +
                (result.Titles || []).join('<br/>');
        });
    }

    return function (view) {
        view.addEventListener('viewshow', function () {
            loadRuleSets(view);

            view.querySelector('#btnAddCondition').addEventListener('click', function () {
                view.querySelector('#conditionsList').appendChild(
                    buildConditionRow(view, {}));
            });

            view.querySelector('#btnSave').addEventListener('click', function () {
                saveRuleSets(view);
            });

            view.querySelector('#btnPreview').addEventListener('click', function () {
                previewRule(view);
            });
        });
    };
});