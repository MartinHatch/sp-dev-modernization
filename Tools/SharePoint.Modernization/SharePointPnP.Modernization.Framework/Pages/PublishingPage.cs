﻿using Microsoft.SharePoint.Client;
using Microsoft.SharePoint.Client.WebParts;
using SharePointPnP.Modernization.Framework.Cache;
using SharePointPnP.Modernization.Framework.Entities;
using SharePointPnP.Modernization.Framework.Publishing;
using SharePointPnP.Modernization.Framework.Telemetry;
using SharePointPnP.Modernization.Framework.Transform;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharePointPnP.Modernization.Framework.Pages
{
    /// <summary>
    /// Analyzes a publishing page
    /// </summary>
    public class PublishingPage : BasePage
    {
        private PublishingPageTransformation publishingPageTransformation;
        private PublishingFunctionProcessor functionProcessor;
        private BaseTransformationInformation baseTransformationInformation;
        private ClientContext targetContext = null;

        #region Construction
        /// <summary>
        /// Instantiates a publishing page object
        /// </summary>
        /// <param name="page">ListItem holding the page to analyze</param>
        /// <param name="pageTransformation">Page transformation information</param>
        public PublishingPage(ListItem page, PageTransformation pageTransformation, BaseTransformationInformation baseTransformationInformation, IList<ILogObserver> logObservers = null) : base(page, null, pageTransformation, logObservers)
        {
            // no PublishingPageTransformation specified, fall back to default
            this.publishingPageTransformation = new PageLayoutManager(base.RegisteredLogObservers).LoadDefaultPageLayoutMappingFile();
            this.baseTransformationInformation = baseTransformationInformation;
            this.functionProcessor = new PublishingFunctionProcessor(page, cc, null, this.publishingPageTransformation, baseTransformationInformation, base.RegisteredLogObservers);
        }

        /// <summary>
        /// Instantiates a publishing page object
        /// </summary>
        /// <param name="page">ListItem holding the page to analyze</param>
        /// <param name="pageTransformation">Page transformation information</param>
        public PublishingPage(ListItem page, PageTransformation pageTransformation, PublishingPageTransformation publishingPageTransformation, BaseTransformationInformation baseTransformationInformation, ClientContext targetContext = null, IList<ILogObserver> logObservers = null) : base(page, null, pageTransformation, logObservers)
        {
            this.publishingPageTransformation = publishingPageTransformation;
            this.baseTransformationInformation = baseTransformationInformation;
            this.targetContext = targetContext;
            this.functionProcessor = new PublishingFunctionProcessor(page, cc, targetContext, this.publishingPageTransformation, baseTransformationInformation, base.RegisteredLogObservers);
        }
        #endregion

        /// <summary>
        /// Analyses a publishing page
        /// </summary>
        /// <returns>Information about the analyzed publishing page</returns>
        public Tuple<PageLayout, List<WebPartEntity>> Analyze(Publishing.PageLayout publishingPageTransformationModel)
        {
            List<WebPartEntity> webparts = new List<WebPartEntity>();

            //Load the page
            var publishingPageUrl = page[Constants.FileRefField].ToString();
            var publishingPage = cc.Web.GetFileByServerRelativeUrl(publishingPageUrl);

            // Load relevant model data for the used page layout in case not already provided - safetynet for calls from modernization scanner
            string usedPageLayout = System.IO.Path.GetFileNameWithoutExtension(page.PageLayoutFile());
            if (publishingPageTransformationModel == null)
            {
                publishingPageTransformationModel = this.publishingPageTransformation.PageLayouts.Where(p => p.Name.Equals(usedPageLayout, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();

                // No layout provided via either the default mapping or custom mapping file provided
                if (publishingPageTransformationModel == null)
                {
                    publishingPageTransformationModel = CacheManager.Instance.GetPageLayoutMapping(page);
                }
            }

            // Still no layout...can't continue...
            if (publishingPageTransformationModel == null)
            {
                LogError(string.Format(LogStrings.Error_NoPageLayoutTransformationModel, usedPageLayout), LogStrings.Heading_PublishingPage);
                throw new Exception(string.Format(LogStrings.Error_NoPageLayoutTransformationModel, usedPageLayout));
            }

            // Map layout
            PageLayout layout = MapToLayout(publishingPageTransformationModel.PageLayoutTemplate);

            #region Process fields that become web parts 
            if (publishingPageTransformationModel.WebParts != null)
            {
                #region Publishing Html column processing
                // Converting to WikiTextPart is a special case as we'll need to process the html
                var wikiTextWebParts = publishingPageTransformationModel.WebParts.Where(p => p.TargetWebPart.Equals(WebParts.WikiText, StringComparison.InvariantCultureIgnoreCase));
                List<WebPartPlaceHolder> webPartsToRetrieve = new List<WebPartPlaceHolder>();
                foreach (var wikiTextPart in wikiTextWebParts)
                {
                    var pageContents = page.GetFieldValueAs<string>(wikiTextPart.Name);
                    if (pageContents != null && !string.IsNullOrEmpty(pageContents))
                    {
                        var htmlDoc = parser.Parse(pageContents);

                        // Analyze the html block (which is a wiki block)
                        var content = htmlDoc.FirstElementChild.LastElementChild;
                        AnalyzeWikiContentBlock(webparts, htmlDoc, webPartsToRetrieve, wikiTextPart.Row, wikiTextPart.Column, GetNextOrder(wikiTextPart.Row, wikiTextPart.Column, wikiTextPart.Order, webparts), content);
                    }
                    else
                    {
                        LogWarning(LogStrings.Warning_CannotRetrieveFieldValue, LogStrings.Heading_PublishingPage);
                    }
                }

                // Bulk load the needed web part information
                if (webPartsToRetrieve.Count > 0)
                {
                    LoadWebPartsInWikiContentFromServer(webparts, publishingPage, webPartsToRetrieve);
                }
                #endregion

                #region Generic processing of the other 'webpart' fields
                var fieldWebParts = publishingPageTransformationModel.WebParts.Where(p => !p.TargetWebPart.Equals(WebParts.WikiText, StringComparison.InvariantCultureIgnoreCase));
                foreach (var fieldWebPart in fieldWebParts.OrderBy(p => p.Row).OrderBy(p => p.Column))
                {
                    // In publishing scenarios it's common to not have all fields defined in the page layout mapping filled. By default we'll not map empty fields as that will result in empty web parts
                    // which impact the page look and feel. Using the RemoveEmptySectionsAndColumns flag this behaviour can be turned off.
                    if (this.baseTransformationInformation.RemoveEmptySectionsAndColumns)
                    {
                        var fieldContents = page.GetFieldValueAs<string>(fieldWebPart.Name);

                        if (string.IsNullOrEmpty(fieldContents))
                        {
                            LogWarning(String.Format(LogStrings.Warning_SkippedWebPartDueToEmptyInSourcee, fieldWebPart.TargetWebPart, fieldWebPart.Name), LogStrings.Heading_PublishingPage);
                            continue;
                        }
                    }

                    Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

                    foreach (var fieldWebPartProperty in fieldWebPart.Property)
                    {
                        if (!string.IsNullOrEmpty(fieldWebPartProperty.Functions))
                        {
                            // execute function
                            var evaluatedField = this.functionProcessor.Process(fieldWebPartProperty.Functions, fieldWebPartProperty.Name, MapToFunctionProcessorFieldType(fieldWebPartProperty.Type));
                            if (!string.IsNullOrEmpty(evaluatedField.Item1) && !properties.ContainsKey(evaluatedField.Item1))
                            {
                                properties.Add(evaluatedField.Item1, evaluatedField.Item2);
                            }
                        }
                        else
                        {
                            var webPartName = page.FieldValues[fieldWebPart.Name]?.ToString().Trim();
                            if (webPartName != null)
                            {
                                properties.Add(fieldWebPartProperty.Name, page.FieldValues[fieldWebPart.Name].ToString().Trim());
                            }
                        }
                    }

                    var wpEntity = new WebPartEntity()
                    {
                        Title = fieldWebPart.Name,
                        Type = fieldWebPart.TargetWebPart,
                        Id = Guid.Empty,
                        Row = fieldWebPart.Row,
                        Column = fieldWebPart.Column,
                        Order = GetNextOrder(fieldWebPart.Row, fieldWebPart.Column, fieldWebPart.Order, webparts),
                        Properties = properties,
                    };

                    webparts.Add(wpEntity);
                }
            }
            #endregion
            #endregion

            #region Process fields that become metadata as they might result in the creation of page properties web part
            if (publishingPageTransformationModel.MetaData.ShowPageProperties)
            {
                List<string> pagePropertiesFields = new List<string>();
               
                var fieldsToProcess = publishingPageTransformationModel.MetaData.Field.Where(p => p.ShowInPageProperties == true && !string.IsNullOrEmpty(p.TargetFieldName));

                if (fieldsToProcess.Any())
                {                    
                    // Loop over the fields that are defined to be shown in the page properties and that have a target field name set
                    foreach (var fieldToProcess in fieldsToProcess)
                    {
                        var targetFieldInstance = targetContext.Web.GetFieldByInternalName(fieldToProcess.TargetFieldName, true);
                        if (targetFieldInstance != null)
                        {
                            if (!pagePropertiesFields.Contains(targetFieldInstance.Id.ToString()))
                            {
                                pagePropertiesFields.Add(targetFieldInstance.Id.ToString());
                            }
                        }
                    }

                    if (pagePropertiesFields.Count > 0)
                    {
                        string propertyString = "";
                        foreach (var propertyField in pagePropertiesFields)
                        {
                            if (!string.IsNullOrEmpty(propertyField))
                            {
                                propertyString = $"{propertyString},\"{propertyField.ToString()}\"";
                            }
                        }

                        if (!string.IsNullOrEmpty(propertyString))
                        {
                            propertyString = propertyString.TrimStart(new char[] { ',' });
                        }

                        if (!string.IsNullOrEmpty(propertyString))
                        {
                            Dictionary<string, string> properties = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase)
                        {
                            { "SelectedFields", propertyString }
                        };

                            var wpEntity = new WebPartEntity()
                            {
                                Type = WebParts.PageProperties,
                                Id = Guid.Empty,
                                Row = publishingPageTransformationModel.MetaData.PagePropertiesRow,
                                Column = publishingPageTransformationModel.MetaData.PagePropertiesColumn,
                                Order = GetNextOrder(publishingPageTransformationModel.MetaData.PagePropertiesRow, publishingPageTransformationModel.MetaData.PagePropertiesColumn, publishingPageTransformationModel.MetaData.PagePropertiesOrder, webparts),
                                Properties = properties,
                            };

                            webparts.Add(wpEntity);
                        }
                    }
                }
            }
            #endregion

            #region Web Parts in webpart zone handling
            // Load web parts put in web part zones on the publishing page
            // Note: Web parts placed outside of a web part zone using SPD are not picked up by the web part manager. 
            var limitedWPManager = publishingPage.GetLimitedWebPartManager(PersonalizationScope.Shared);
            cc.Load(limitedWPManager);

            IEnumerable<WebPartDefinition> webPartsViaManager = cc.LoadQuery(limitedWPManager.WebParts.IncludeWithDefaultProperties(wp => wp.Id, wp => wp.ZoneId, wp => wp.WebPart.ExportMode, wp => wp.WebPart.Title, wp => wp.WebPart.ZoneIndex, wp => wp.WebPart.IsClosed, wp => wp.WebPart.Hidden, wp => wp.WebPart.Properties));
            cc.ExecuteQueryRetry();

            if (webPartsViaManager.Count() > 0)
            {
                List<WebPartPlaceHolder> webPartsToRetrieve = new List<WebPartPlaceHolder>();

                foreach (var foundWebPart in webPartsViaManager)
                {
                    // Remove the web parts which we've already picked up by analyzing the wiki content block
                    if (webparts.Where(p => p.Id.Equals(foundWebPart.Id)).FirstOrDefault() != null)
                    {
                        continue;
                    }

                    webPartsToRetrieve.Add(new WebPartPlaceHolder()
                    {
                        WebPartDefinition = foundWebPart,
                        WebPartXml = null,
                        WebPartType = "",
                    });
                }

                bool isDirty = false;
                foreach (var foundWebPart in webPartsToRetrieve)
                {
                    if (foundWebPart.WebPartDefinition.WebPart.ExportMode == WebPartExportMode.All)
                    {
                        foundWebPart.WebPartXml = limitedWPManager.ExportWebPart(foundWebPart.WebPartDefinition.Id);
                        isDirty = true;
                    }
                }
                if (isDirty)
                {
                    cc.ExecuteQueryRetry();
                }

                foreach (var foundWebPart in webPartsToRetrieve.OrderBy(p=> p.WebPartDefinition.WebPart.ZoneIndex))
                {
                    if (foundWebPart.WebPartDefinition.WebPart.ExportMode != WebPartExportMode.All)
                    {
                        // Use different approach to determine type as we can't export the web part XML without indroducing a change
                        foundWebPart.WebPartType = GetTypeFromProperties(foundWebPart.WebPartDefinition.WebPart.Properties);
                    }
                    else
                    {
                        foundWebPart.WebPartType = GetType(foundWebPart.WebPartXml.Value);
                    }

                    int wpInZoneRow = 1;
                    int wpInZoneCol = 1;
                    int wpStartOrder = 0;
                    // Determine location based upon the location given to the web part zone in the mapping
                    if (publishingPageTransformationModel.WebPartZones != null)
                    {
                        var wpZoneFromTemplate = publishingPageTransformationModel.WebPartZones.Where(p => p.ZoneId.Equals(foundWebPart.WebPartDefinition.ZoneId, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
                        if (wpZoneFromTemplate != null)
                        {
                            wpInZoneRow = wpZoneFromTemplate.Row;
                            wpInZoneCol = wpZoneFromTemplate.Column;
                            wpStartOrder = wpZoneFromTemplate.Order;
                        }
                    }

                    // Determine order already taken
                    int wpInZoneOrderUsed = GetNextOrder(wpInZoneRow, wpInZoneCol, wpStartOrder, webparts);

                    webparts.Add(new WebPartEntity()
                    {
                        Title = foundWebPart.WebPartDefinition.WebPart.Title,
                        Type = foundWebPart.WebPartType,
                        Id = foundWebPart.WebPartDefinition.Id,
                        ServerControlId = foundWebPart.WebPartDefinition.Id.ToString(),
                        Row = wpInZoneRow,
                        Column = wpInZoneCol,
                        Order = wpInZoneOrderUsed + foundWebPart.WebPartDefinition.WebPart.ZoneIndex,
                        ZoneId = foundWebPart.WebPartDefinition.ZoneId,
                        ZoneIndex = (uint)foundWebPart.WebPartDefinition.WebPart.ZoneIndex,
                        IsClosed = foundWebPart.WebPartDefinition.WebPart.IsClosed,
                        Hidden = foundWebPart.WebPartDefinition.WebPart.Hidden,
                        Properties = Properties(foundWebPart.WebPartDefinition.WebPart.Properties, foundWebPart.WebPartType, foundWebPart.WebPartXml == null ? "" : foundWebPart.WebPartXml.Value),
                    });
                }
            }
            #endregion

            #region Fixed webparts mapping
            if (publishingPageTransformationModel.FixedWebParts != null)
            {
                foreach(var fixedWebpart in publishingPageTransformationModel.FixedWebParts)
                {
                    int wpFixedOrderUsed = GetNextOrder(fixedWebpart.Row, fixedWebpart.Column, fixedWebpart.Order, webparts);

                    webparts.Add(new WebPartEntity()
                    {
                        Title = GetFixedWebPartProperty<string>(fixedWebpart, "Title", ""),
                        Type = fixedWebpart.Type,
                        Id = Guid.NewGuid(),
                        Row = fixedWebpart.Row,
                        Column = fixedWebpart.Column,
                        Order = wpFixedOrderUsed,
                        ZoneId = "",
                        ZoneIndex = 0,
                        IsClosed = GetFixedWebPartProperty<bool>(fixedWebpart, "__designer:IsClosed", false),
                        Hidden = false,
                        Properties = CastAsPropertiesDictionary(fixedWebpart),
                    });

                }
            }
            #endregion

            return new Tuple<PageLayout, List<WebPartEntity>>(layout, webparts);
        }

        #region Helper methods
        private T GetFixedWebPartProperty<T>(FixedWebPart webPart, string name, T defaultValue)
        {
            var property = webPart.Property.Where(p => p.Name.Equals(name, StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
            if (property != null)
            {

                if (property.Value.StartsWith("$Resources:"))
                {
                    property.Value = CacheManager.Instance.GetResourceString(this.cc, property.Value);
                }

                if (property.Value is T)
                {
                    return (T)(object)property.Value;
                }
                try
                {
                    return (T)Convert.ChangeType(property.Value, typeof(T));
                }
                catch (InvalidCastException)
                {
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        private Dictionary<string, string> CastAsPropertiesDictionary(FixedWebPart webPart)
        {
            Dictionary<string, string> props = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);

            foreach(var prop in webPart.Property)
            {
                props.Add(prop.Name, prop.Value);
            }

            return props;
        }

        private int GetNextOrder(int row, int col, int order, List<WebPartEntity> webparts)
        {
            // do we already have web parts in the same row and column
            var wp = webparts.Where(p => p.Row == row && p.Column == col);

            if (order > 0)
            {
                // Multiply with 100 to leave space for possible multiple web parts living in an ordered web part zone
                return order * 100;
            }
            else
            {
                if (wp != null && wp.Any())
                {
                    var lastWp = wp.OrderBy(p => p.Order).Last();
                    return lastWp.Order + 1;
                }
                else
                {
                    return 1;
                }
            }
        }

        private PageLayout MapToLayout(PageLayoutPageLayoutTemplate layoutFromTemplate)
        {
            switch (layoutFromTemplate)
            {
                case PageLayoutPageLayoutTemplate.OneColumn: return PageLayout.Wiki_OneColumn;
                case PageLayoutPageLayoutTemplate.TwoColumns: return PageLayout.Wiki_TwoColumns;
                case PageLayoutPageLayoutTemplate.TwoColumnsWithSidebarLeft:return PageLayout.Wiki_TwoColumnsWithSidebar;
                case PageLayoutPageLayoutTemplate.TwoColumnsWithSidebarRight: return PageLayout.Wiki_TwoColumnsWithSidebar;
                case PageLayoutPageLayoutTemplate.TwoColumnsWithHeader: return PageLayout.Wiki_TwoColumnsWithHeader;
                case PageLayoutPageLayoutTemplate.TwoColumnsWithHeaderAndFooter: return PageLayout.Wiki_TwoColumnsWithHeaderAndFooter;
                case PageLayoutPageLayoutTemplate.ThreeColumns: return PageLayout.Wiki_ThreeColumns;
                case PageLayoutPageLayoutTemplate.ThreeColumnsWithHeader: return PageLayout.Wiki_ThreeColumnsWithHeader;
                case PageLayoutPageLayoutTemplate.ThreeColumnsWithHeaderAndFooter: return PageLayout.Wiki_ThreeColumnsWithHeaderAndFooter;
                case PageLayoutPageLayoutTemplate.AutoDetect: return PageLayout.PublishingPage_AutoDetect;
                default: return PageLayout.Wiki_OneColumn;
            }
        }

        private PublishingFunctionProcessor.FieldType MapToFunctionProcessorFieldType(WebPartProperyType propertyType)
        {
            switch (propertyType)
            {
                case WebPartProperyType.@string: return PublishingFunctionProcessor.FieldType.String;
                case WebPartProperyType.@bool: return PublishingFunctionProcessor.FieldType.Bool;
                case WebPartProperyType.guid:return PublishingFunctionProcessor.FieldType.Guid;
                case WebPartProperyType.integer: return PublishingFunctionProcessor.FieldType.Integer;
                case WebPartProperyType.datetime: return PublishingFunctionProcessor.FieldType.DateTime;
            }

            return PublishingFunctionProcessor.FieldType.String;
            #endregion
        }
    }
}